﻿/*
Copyright 2009-2021 Intel Corporation

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Security.Cryptography.X509Certificates;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Windows.Forms;
using System.Drawing;
using CERTENROLLLib;

namespace MeshAssistant
{
    public class MeshCentralAgent
    {
        private MainForm parent;
        public bool debug = false;
        public bool autoConnect = false; // Agent is in auto-connection mode. When enabled, user consent is mandatory.
        public int autoConnectTime = 5; // Number of seconds to next connection attempt.
        private System.Threading.Timer autoConnectTimer = null;
        public int state = 0;
        public string HelpRequest = null;
        private byte[] MeshId = null;
        private string ServerId = null;
        private byte[] ServerTlsHash = null;
        private string ServerTlsHashStr = null;
        private Uri ServerUrl = null;
        private byte[] Nonce = null;
        private byte[] ServerNonce = null;
        private X509Certificate2 agentCert = null;
        private JavaScriptSerializer JSON = new JavaScriptSerializer();
        public webSocketClient WebSocket = null;
        private int ConnectionState = 0;
        private List<MeshCentralTunnel> tunnels = new List<MeshCentralTunnel>(); // List of active tunnels
        private List<MeshCentralTcpTunnel> tcptunnels = new List<MeshCentralTcpTunnel>(); // List of active TCP tunnels
        public Dictionary<string, Image> userimages = new Dictionary<string, Image>(); // UserID --> Image
        public Dictionary<string, string> userrealname = new Dictionary<string, string>(); // UserID --> Realname
        private string softwareName = null;
        private string selfExecutableHashHex = null;
        public string privacyBarText = null;
        private Random rand = new Random();

        // Sessions
        public Dictionary<string, object> DesktopSessions = null;
        public Dictionary<string, object> TerminalSessions = null;
        public Dictionary<string, object> FilesSessions = null;
        public Dictionary<string, object> TcpSessions = null;
        public Dictionary<string, object> UdpSessions = null;
        public Dictionary<string, object> MessagesSessions = null;

        public delegate void onUserInfoChangeHandler(string userid, int change); // Change: 1 = Image, 2 = Realname, 3 = PrivacyText
        public event onUserInfoChangeHandler onUserInfoChange;
        public static string getSelfFilename(string ext) { string s = Process.GetCurrentProcess().MainModule.FileName; return s.Substring(0, s.Length - 4) + ext; }

        public delegate void onSelfUpdateHandler(string name, string hash, string url, string serverhash);
        public event onSelfUpdateHandler onSelfUpdate;

        public delegate void onRequestConsentHandler(MeshCentralTunnel tunnel, string msg, int protocol, string userid);
        public event onRequestConsentHandler onRequestConsent;

        public void askForConsent(MeshCentralTunnel tunnel, string msg, int protocol, string userid)
        {
            if (onRequestConsent != null) { onRequestConsent(tunnel, msg, protocol, userid); }
        }

        public bool doesUserHaveSession(string userid, int protocol)
        {
            lock (tunnels) { foreach (MeshCentralTunnel tunnel in tunnels) { if ((tunnel.protocol == protocol) && (tunnel.sessionUserName == userid) && (tunnel.consentRequested == false)) return true; } }
            return false;
        }

        public delegate void onSessionChangedHandler();
        public event onSessionChangedHandler onSessionChanged;
        public void fireSessionChanged(int protocol) {
            if (onSessionChanged != null) { onSessionChanged(); }
            if (WebSocket == null) return;

            // Update the server with latest session
            if (protocol == 2)
            {
                string r = "{\"action\":\"sessions\",\"type\":\"kvm\",\"value\":{";
                if (DesktopSessions != null)
                {
                    bool first = true;
                    foreach (string userid in DesktopSessions.Keys)
                    {
                        if (first) { first = false; } else { r += ","; }
                        r += "\"" + userid + "\":" + (int)DesktopSessions[userid];
                    }
                }
                r += "}}";
                if (WebSocket != null) { WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes(r)); }
            }
            else if (protocol == 5)
            {
                string r = "{\"action\":\"sessions\",\"type\":\"files\",\"value\":{";
                if (FilesSessions != null) {
                    bool first = true;
                    foreach (string userid in FilesSessions.Keys)
                    {
                        if (first) { first = false; } else { r += ","; }
                        r += "\"" + userid + "\":" + (int)FilesSessions[userid];
                    }
                }
                r += "}}";
                if (WebSocket != null) { WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes(r)); }
            }
        }

        public MeshCentralAgent(MainForm parent, string softwareName, string selfExecutableHashHex, bool debug)
        {
            this.debug = debug;
            this.parent = parent;
            this.softwareName = softwareName;
            this.selfExecutableHashHex = selfExecutableHashHex;

            // Load the agent certificate and private key
            agentCert = LoadAgentCertificate();

            // Load the MSH file
            Dictionary<string, string> msh = LoadMshFile();

            // Get the MeshId, ServerId, and ServerUrl
            if (msh.ContainsKey("MeshID")) { string m = msh["MeshID"]; if (m.StartsWith("0x")) { m = m.Substring(2); } MeshId = StringToByteArray(m); }
            if (msh.ContainsKey("ServerID")) { ServerId = msh["ServerID"]; }
            if (msh.ContainsKey("MeshServer")) { try { ServerUrl = new Uri(msh["MeshServer"]); } catch (Exception) { } }
            if (msh.ContainsKey("AutoConnect")) { parent.autoConnect = (msh["AutoConnect"] == "1"); }
            Log("MSH MeshID: " + msh["MeshID"]);
            Log("MSH ServerID: " + ServerId);
            Log("MSH MeshServer: " + ServerUrl);
        }

        public static bool checkMshFile()
        {
            // Load the MSH file
            Dictionary<string, string> msh = LoadMshFile();
            if (msh == null) return false;
            if (!msh.ContainsKey("MeshID")) return false;
            if (!msh.ContainsKey("ServerID")) return false;
            if (!msh.ContainsKey("MeshServer")) return false;
            return true;
        }

        private X509Certificate2 LoadAgentCertificate()
        {
            string p12filename = getSelfFilename(".p12");
            X509Certificate2 cert = null;
            if (File.Exists(p12filename)) { try { cert = new X509Certificate2(p12filename, "dummy"); } catch (Exception) { } }
            if (cert != null) { Log("LoadAgentCertificate() - Loaded existing certificate."); return cert; }

            try
            {
                Log("LoadAgentCertificate() - Creating new certificate...");
                RSA rsa = RSA.Create(3072); // Generate asymmetric RSA key pair
                CertificateRequest req = new CertificateRequest("cn=MeshAgent", rsa, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);
                cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-10), DateTimeOffset.Now.AddYears(20));
                File.WriteAllBytes(p12filename, cert.Export(X509ContentType.Pkcs12, "dummy"));
                Log("LoadAgentCertificate() - Certificate created");
                return cert;
            } catch (Exception) { }

            Log("LoadAgentCertificate() - Creating new certificate using CertEnroll...");
            cert = CreateSelfSignedCertificate("MeshAgent");
            File.WriteAllBytes(p12filename, cert.Export(X509ContentType.Pkcs12, "dummy"));
            Log("LoadAgentCertificate() - Certificate created");
            return cert;
        }

        public static X509Certificate2 CreateSelfSignedCertificate(string subjectName)
        {
            // create DN for subject and issuer
            var dn = new CX500DistinguishedName();
            dn.Encode("CN=" + subjectName, X500NameFlags.XCN_CERT_NAME_STR_NONE);

            // create a new private key for the certificate
            CX509PrivateKey privateKey = new CX509PrivateKey();
            privateKey.ProviderName = "Microsoft Base Cryptographic Provider v1.0";
            privateKey.MachineContext = false;
            privateKey.Length = 2048;
            privateKey.KeySpec = X509KeySpec.XCN_AT_SIGNATURE; // use is not limited
            privateKey.ExportPolicy = X509PrivateKeyExportFlags.XCN_NCRYPT_ALLOW_PLAINTEXT_EXPORT_FLAG;
            privateKey.Create();

            // Use the stronger SHA384 hashing algorithm
            var hashobj = new CObjectId();
            hashobj.InitializeFromAlgorithmName(ObjectIdGroupId.XCN_CRYPT_HASH_ALG_OID_GROUP_ID,
                ObjectIdPublicKeyFlags.XCN_CRYPT_OID_INFO_PUBKEY_ANY,
                AlgorithmFlags.AlgorithmFlagsNone, "SHA384");

            // add extended key usage if you want - look at MSDN for a list of possible OIDs
            var oid = new CObjectId();
            oid.InitializeFromValue("1.3.6.1.5.5.7.3.1"); // SSL server
            var oidlist = new CObjectIds();
            oidlist.Add(oid);
            var eku = new CX509ExtensionEnhancedKeyUsage();
            eku.InitializeEncode(oidlist);

            // Create the self signing request
            var cert = new CX509CertificateRequestCertificate();
            cert.InitializeFromPrivateKey(X509CertificateEnrollmentContext.ContextUser, privateKey, "");
            cert.Subject = dn;
            cert.Issuer = dn; // the issuer and the subject are the same
            cert.NotBefore = DateTime.Now.AddDays(-10);
            cert.NotAfter = DateTime.Now.AddYears(20);
            cert.X509Extensions.Add((CX509Extension)eku); // add the EKU
            cert.HashAlgorithm = hashobj; // Specify the hashing algorithm
            cert.Encode(); // encode the certificate

            // Do the final enrollment process
            var enroll = new CX509Enrollment();
            enroll.InitializeFromRequest(cert); // load the certificate
            enroll.CertificateFriendlyName = subjectName; // Optional: add a friendly name
            string csr = enroll.CreateRequest(); // Output the request in base64
                                                 // and install it back as the response
            enroll.InstallResponse(InstallResponseRestrictionFlags.AllowUntrustedCertificate,
                csr, EncodingType.XCN_CRYPT_STRING_BASE64, ""); // no password
                                                                // output a base64 encoded PKCS#12 so we can import it back to the .Net security classes
            var base64encoded = enroll.CreatePFX("", // no password, this is for internal consumption
                PFXExportOptions.PFXExportChainWithRoot);

            // instantiate the target class with the PKCS#12 data (and the empty password)
            return new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Convert.FromBase64String(base64encoded), "",
                // mark the private key as exportable (this is usually what you want to do)
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable
            );
        }

        private static Dictionary<string, string> LoadMshFile()
        {
            string mshfilename = getSelfFilename(".msh");
            if (!File.Exists(mshfilename)) return null;
            string[] lines = null;
            try { lines = File.ReadAllLines(mshfilename); } catch (Exception) { }
            if (lines == null) return null;
            Dictionary<string, string> vals = new Dictionary<string, string>();
            foreach (string line in lines) { int i = line.IndexOf('='); if (i > 0) { vals.Add(line.Substring(0, i), line.Substring(i + 1)); } }
            return vals;
        }

        public delegate void onNotifyHandler(string userid, string title, string msg);
        public event onNotifyHandler onNotify;
        public void notify(string userid, string title, string msg) { if (onNotify != null) { onNotify(userid, title, msg); } }

        public delegate void onStateChangedHandler(int state);
        public event onStateChangedHandler onStateChanged;
        public void changeState(int newState) { if (state != newState) { state = newState; if (onStateChanged != null) { onStateChanged(state); } } }

        public byte[] GenerateCryptographicRandom(int len)
        {
            RNGCryptoServiceProvider rngCryptoServiceProvider = new RNGCryptoServiceProvider();
            byte[] randomBytes = new byte[len];
            rngCryptoServiceProvider.GetBytes(randomBytes);
            return randomBytes;
        }

        public void Log(string msg)
        {
            if (debug) {
                //Console.WriteLine(msg);
                try { File.AppendAllText("debug.log", DateTime.Now.ToString("HH:mm:tt.ffff") + ": MCAgent: " + msg + "\r\n"); } catch (Exception) { }
            }
        }

        public delegate void onLogEventHandler(DateTime time, string userid, string msg);
        public event onLogEventHandler onLogEvent;

        public void Event(string userid, string msg)
        {
            DateTime now = DateTime.Now;
            if (onLogEvent != null) { onLogEvent(now, userid, msg); }
            try { File.AppendAllText("events.log", now.ToString("yyyy-MM-ddTHH:mm:sszzz") + ", " + userid + ", " + msg + "\r\n"); } catch (Exception) { }
        }

        public static byte[] StringToByteArray(string hex) { return Enumerable.Range(0, hex.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(hex.Substring(x, 2), 16)).ToArray(); }

        public bool connect()
        {
            Log("connect()");
            autoConnectTime = 5;
            if (autoConnectTimer != null) { autoConnectTimer.Dispose(); autoConnectTimer = null; }
            return connectex();
        }

        private bool connectex()
        {
            if ((MeshId == null) || (MeshId.Length != 48)) return false;
            if (WebSocket != null) return false;
            Log(string.Format("Attempting connection to {0}", ServerUrl));
            userimages = new Dictionary<string, Image>(); // UserID --> Image
            userrealname = new Dictionary<string, string>(); // UserID --> Realname
            WebSocket = new webSocketClient();
            WebSocket.pongTimeSeconds = 120; // Send a websocket pong every 2 minutes.
            WebSocket.debug = debug;
            WebSocket.onStateChanged += WebSocket_onStateChanged;
            WebSocket.onBinaryData += WebSocket_onBinaryData;
            WebSocket.onStringData += WebSocket_onStringData;
            ConnectionState = 1;
            return WebSocket.Start(ServerUrl, null);
        }

        private void WebSocket_onStateChanged(webSocketClient sender, webSocketClient.ConnectionStates state)
        {
            if (state == webSocketClient.ConnectionStates.Disconnected)
            {
                disconnectex();
            }
            else if (state == webSocketClient.ConnectionStates.Connecting)
            {
                changeState(1);
            }
            else if (state == webSocketClient.ConnectionStates.Connected)
            {
                changeState(2);
                ConnectionState |= 2;

                // Compute the remote certificate SHA384 hash
                using (SHA384 sha384Hash = SHA384.Create())
                {
                    byte[] bytes = ServerTlsHash = sha384Hash.ComputeHash(WebSocket.RemoteCertificate.GetRawCertData());
                    StringBuilder builder = new StringBuilder();
                    for (int i = 0; i < bytes.Length; i++) { builder.Append(bytes[i].ToString("x2")); }
                    ServerTlsHashStr = builder.ToString().ToUpper();
                }
                Log(string.Format("Websocket TLS hash: {0}", ServerTlsHashStr));

                // Send command 1, hash + nonce
                Nonce = GenerateCryptographicRandom(48);
                BinaryWriter bw = new BinaryWriter(new MemoryStream());
                bw.Write(Convert.ToInt16(IPAddress.HostToNetworkOrder((short)1)));
                bw.Write(ServerTlsHash);
                bw.Write(Nonce);
                WebSocket.SendBinary(((MemoryStream)bw.BaseStream).ToArray());
            }
        }

        public void disconnect()
        {
            Log("Disconnect");
            autoConnect = false;
            autoConnectTime = 5;
            if (autoConnectTimer != null) { autoConnectTimer.Dispose(); autoConnectTimer = null; }
            disconnectex();
        }

        private void disconnectex()
        {
            if (WebSocket == null) return;
            Log("DisconnectEx");
            WebSocket.Dispose();
            WebSocket = null;
            ConnectionState = 0;
            changeState(0);
            foreach (MeshCentralTunnel tunnel in tunnels) { tunnel.disconnect(); }
            foreach (MeshCentralTcpTunnel tcptunnel in tcptunnels) { tcptunnel.disconnect(); }

            // Setup auto-reconnect timer if needed
            if (autoConnect) {
                Log(string.Format("Setting connect retry timer to {0} seconds", autoConnectTime));
                if (autoConnectTimer != null) { autoConnectTimer.Dispose(); autoConnectTimer = null; }
                autoConnectTimer = new System.Threading.Timer(new System.Threading.TimerCallback(reconnectAttempt), null, autoConnectTime * 1000, autoConnectTime * 1000);
            }
        }

        private void reconnectAttempt(object state)
        {
            Log("ReconnectAttempt");
            if (autoConnectTimer != null) { autoConnectTimer.Dispose(); autoConnectTimer = null; }
            autoConnectTime = autoConnectTime + rand.Next(4, autoConnectTime);
            if (autoConnectTime > 1200) { autoConnectTime = 1200; }
            connectex();
        }

        public void disconnectAllTunnels()
        {
            foreach (MeshCentralTunnel tunnel in tunnels) { tunnel.disconnect(); }
        }

        private void serverConnected()
        {
            Log("Server Connected");
            autoConnectTime = 5;

            // Update network information
            sendNetworkInfo();

            // Send help session
            if (HelpRequest != null)
            {
                string userName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                sendSessionUpdate("help", "{\"" + escapeJsonString(userName) + "\":\"" + escapeJsonString(HelpRequest) + "\"}");
                sendHelpEventLog(userName, HelpRequest);
            }

            // Send self-update query
            if ((softwareName != null) && (selfExecutableHashHex != null))
            {
                sendSelfUpdateQuery(softwareName, selfExecutableHashHex);
            }
        }

        public void RequestHelp(string HelpRequest)
        {
            if (this.HelpRequest == HelpRequest) return;
            this.HelpRequest = HelpRequest;
            if (HelpRequest != null) {
                // Send help request
                string userName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                sendSessionUpdate("help", "{\"" + escapeJsonString(userName) + "\":\"" + escapeJsonString(HelpRequest) + "\"}");
                sendHelpEventLog(userName, HelpRequest);
                Event("local", "Requested help for \"" + HelpRequest.Replace("\r\n", " ") + "\"");
            } else {
                // Clear help request
                sendSessionUpdate("help", "{}");
                Event("local", "Canceled help request");
            }
        }

        private void sendSessionUpdate(string type, string value)
        {
            Log(string.Format("sendSessionUpdate {0}, {1}", type, value));
            WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"sessions\",\"type\":\"" + type + "\",\"value\":" + value + "}"));
        }

        private void sendConsoleEventLog(string cmd)
        {
            Log(string.Format("sendConsoleEventLog {0}", cmd));
            WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"log\",\"msgid\":17,\"msgArgs\":[\"" + escapeJsonString(cmd) + "\"],\"msg\":\"Processing console command: " + escapeJsonString(cmd) + "\"}"));
        }

        private void sendOpenUrlEventLog(string url)
        {
            Log(string.Format("sendOpenUrlEventLog {0}", url));
            WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"log\",\"msgid\":20,\"msgArgs\":[\"" + escapeJsonString(url) + "\"],\"msg\":\"Opening: " + escapeJsonString(url) + "\"}"));
        }

        private void sendHelpEventLog(string username, string helpstring)
        {
            Log(string.Format("sendHelpEventLog {0}, {1}", username, helpstring));
            WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"log\",\"msgid\":98,\"msgArgs\":[\"" + escapeJsonString(username) + "\",\"" + escapeJsonString(helpstring) + "\"],\"msg\":\"Help Requested, user: " + escapeJsonString(username) + ", details: " + escapeJsonString(helpstring) + "\"}"));
        }

        private void sendSelfUpdateQuery(string softwareName, string softwareHash)
        {
            Log(string.Format("sendSelfUpdateQuery {0}, {1}", softwareName, softwareHash));
            WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"meshToolInfo\",\"name\":\"" + escapeJsonString(softwareName) + "\",\"hash\":\"" + escapeJsonString(softwareHash) + "\",\"cookie\":true}"));
        }

        private string[] parseArgString(string cmd) {
            bool q = false;
            string acc = "";
            List<string> r = new List<string>();
            foreach (var c in cmd) { if ((c == ' ') && (q == false)) { if (acc.Length > 0) { r.Add(acc); acc = ""; } } else { if (c == '"') { q = !q; } else { acc += c; } } }
            if (acc.Length > 0) { r.Add(acc); }
            return r.ToArray();
        }

        private string fixMacAddress(string mac)
        {
            if (mac == "") return "00:00:00:00:00:00";
            if (mac.Length == 12) return mac.Substring(0, 2) + ":" + mac.Substring(2, 2) + ":" + mac.Substring(4, 2) + ":" + mac.Substring(6, 2) + ":" + mac.Substring(8, 2) + ":" + mac.Substring(10, 2);
            return mac;
        }

        private string escapeJsonString(string str) {
            var r = "";
            foreach (char c in str)
            {
                if (c == '\r') { r += "\\r"; }
                else if (c == '\n') { r += "\\n"; }
                else if (c == '\\') { r += "\\\\"; }
                else if (c == '\"') { r += "\\\""; }
                else { r += c; }
            }
            return r;
        }

        private void sendNetworkInfo()
        {
            Log("sendNetworkInfo");
            WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"netinfo\",\"netif2\":" + getNetworkInfo() + "}"));
        }

        private void getUserImage(string userid)
        {
            Log(string.Format("getUserImage {0}", userid));
            WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"getUserImage\",\"userid\":\"" + escapeJsonString(userid) + "\"}"));
        }

        private string getNetworkInfo()
        {
            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
            string response = "{";
            bool firstItem1 = true;
            foreach (NetworkInterface adapter in adapters)
            {
                if (firstItem1 == false) { response += ","; }
                response += "\"" + escapeJsonString(adapter.Name) + "\":[";
                IPInterfaceProperties properties = adapter.GetIPProperties();
                int i = 0;
                bool firstItem2 = true;
                foreach (UnicastIPAddressInformation addr in properties.UnicastAddresses)
                {
                    if (firstItem2 == false) { response += ","; }
                    response += "{";
                    response += "\"address\":\"" + addr.Address.ToString() + "\",";
                    response += "\"fqdn\":\"" + properties.DnsSuffix + "\",";
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork) { response += "\"family\":\"IPv4\","; }
                    if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6) { response += "\"family\":\"IPv6\","; }
                    response += "\"mac\":\"" + fixMacAddress(adapter.GetPhysicalAddress().ToString()) + "\",";
                    response += "\"index\":\"" + adapter.GetIPProperties().GetIPv6Properties().Index + "\",";
                    response += "\"type\":\"" + adapter.NetworkInterfaceType.ToString().ToLower() + "\",";
                    response += "\"status\":\"" + adapter.OperationalStatus.ToString().ToLower() + "\"";
                    response += "}";
                    firstItem2 = false;
                    i++;
                }
                response += "]";
                firstItem1 = false;
            }
            response += "}";
            Log("getNetworkInfo: " + response);
            return response;
        }

        public void processConsoleCommand(string rawcmd, string sessionid, string userid)
        {
            Log(string.Format("processConsoleCommand, cmd=\"{0}\", sessionid=\"{1}\", userid=\"{2}\"", rawcmd, sessionid, userid));
            Event(userid, string.Format("Console command: {0}", rawcmd));

            string response = null;
            string[] cmd = parseArgString(rawcmd);
            if (cmd.Length == 0) return;
            sendConsoleEventLog(rawcmd);

            switch (cmd[0])
            {
                case "help": {
                        response = "Available commands: \r\n" + "args, help, netinfo, notify, openurl" + ".";
                        break;
                    }
                case "args":
                    {
                        response = "Command arguments: ";
                        foreach (var arg in cmd) { response += ("[" + arg + "]"); }
                        response += "\r\n";
                        break;
                    }
                case "netinfo":
                    {
                        response = getNetworkInfo();
                        break;
                    }
                case "openurl":
                    {
                        if (cmd.Length != 2) { response = "Usage: openurl [url]"; } else { Process.Start(cmd[1]); sendOpenUrlEventLog(cmd[1]); response = "Ok"; }
                        break;
                    }
                case "notify":
                    {
                        if (cmd.Length != 3) { response = "Usage: notify [title] [message]"; } else { notify(userid, cmd[1], cmd[2]); response = "Ok"; }
                        break;
                    }
                default:
                    {
                        response = "Unknown command \"" + cmd[0] + "\", type \"help\" for list of avaialble commands.";
                        break;
                    }
            }

            if (response != null) {
                WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"msg\",\"type\":\"console\",\"value\":\"" + escapeJsonString(response) + "\",\"sessionid\":\"" + sessionid + "\"}"));
            }
        }

        public void PrivacyTextChanged()
        {
            Log("PrivacyTextChanged");
            if (onUserInfoChange != null) { onUserInfoChange(null, 3); } // Event the privacy text change
        }

        public void processServerJsonData(string data)
        {
            Log("processServerJsonData: " + data);

            // Parse the received JSON
            Dictionary<string, object> jsonAction = new Dictionary<string, object>();
            try { jsonAction = JSON.Deserialize<Dictionary<string, object>>(data); } catch (Exception) { return; }
            if (jsonAction == null || jsonAction["action"].GetType() != typeof(string)) return;
            string userid = null;
            if (jsonAction.ContainsKey("userid") && (jsonAction["userid"].GetType() == typeof(string))) { userid = (string)jsonAction["userid"]; }
            if ((userid != null) && (jsonAction.ContainsKey("realname")) && (jsonAction["realname"].GetType() == typeof(string))) { userrealname[userid] = (string)jsonAction["realname"]; }

            // Handle the JSON command
            string action = jsonAction["action"].ToString();
            switch (action)
            {
                case "ping": { WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"pong\"}")); break; }
                case "pong": { break; }
                case "errorlog": { break; }
                case "sysinfo": { break; }
                case "coredump": { break; }
                case "getcoredump": { break; }
                case "getUserImage":
                    {
                        if (userid == null) return;

                        // Get the image
                        string imagestr = null;
                        if (jsonAction.ContainsKey("image") && (jsonAction["image"].GetType() == typeof(string))) { imagestr = (string)jsonAction["image"]; }
                        Image userImage = null;
                        if ((imagestr != null) && (imagestr.StartsWith("data:image/jpeg;base64,")))
                        {
                            // Decode the image
                            try { userImage = Image.FromStream(new MemoryStream(Convert.FromBase64String(imagestr.Substring(23)))); } catch (Exception) { }
                        }
                        if (userImage != null) { userimages[userid] = userImage; if (onUserInfoChange != null) { onUserInfoChange(userid, 1); } } // Send and event the user image
                        else { userimages[userid] = null; } // Indicate that no user image
                        break;
                    }
                case "openUrl":
                    {
                        if (!jsonAction.ContainsKey("url") || (jsonAction["url"].GetType() != typeof(string))) return;
                        Event(userid, string.Format("Opening URL: {0}", jsonAction["url"].ToString()));
                        sendOpenUrlEventLog(jsonAction["url"].ToString());
                        Process.Start(jsonAction["url"].ToString());
                        break;
                    }
                case "msg": {
                        if (!jsonAction.ContainsKey("type") || (jsonAction["type"].GetType() != typeof(string))) return;
                        string eventType = jsonAction["type"].ToString();
                        switch (eventType)
                        {
                            case "console": {
                                    if ((!jsonAction.ContainsKey("value")) && (jsonAction["value"].GetType() != typeof(string))) break;
                                    if ((!jsonAction.ContainsKey("sessionid")) && (jsonAction["sessionid"].GetType() != typeof(string))) break;
                                    string cmd = jsonAction["value"].ToString();
                                    string sessionid = jsonAction["sessionid"].ToString();
                                    processConsoleCommand(cmd, sessionid, userid);
                                    break;
                                }
                            case "localapp":
                                {
                                    if ((jsonAction.ContainsKey("value")) && (jsonAction["value"].GetType() == typeof(Dictionary<string, object>))) {
                                        Dictionary<string, object> localappvalue = (Dictionary<string, object>)jsonAction["value"];
                                        if (localappvalue["cmd"].GetType() == typeof(string))
                                        {
                                            string cmdvalue = localappvalue["cmd"].ToString();
                                            if (cmdvalue == "cancelhelp") {
                                                if (parent.autoConnect == false) {
                                                    disconnect();
                                                } else {
                                                    HelpRequest = null;
                                                    sendSessionUpdate("help", "{}"); // Clear help request
                                                    if (onStateChanged != null) { onStateChanged(state); }
                                                }
                                            }
                                        }
                                    }
                                    break;
                                }
                            case "messagebox":
                                {
                                    // Check if we have a user image, if not, request it and update user real name if available
                                    if ((userid != null) && (!userimages.ContainsKey(userid))) { getUserImage(userid); }
                                    if ((jsonAction.ContainsKey("title")) && (jsonAction["title"].GetType() == typeof(string)) && (jsonAction.ContainsKey("msg")) && (jsonAction["msg"].GetType() == typeof(string)))
                                    {
                                        string title = (string)jsonAction["title"];
                                        string message = (string)jsonAction["msg"];
                                        Event(userid, string.Format("Message box: {0}, {1}", title, message));
                                        notify(userid, title, message);
                                    }
                                    break;
                                }
                            case "tunnel":
                                {
                                    // Check if we have a user image, if not, request it and update user real name if available
                                    if ((userid != null) && (!userimages.ContainsKey(userid))) { getUserImage(userid); }

                                    // Start the new tunnel
                                    if ((jsonAction.ContainsKey("value")) && (jsonAction["value"].GetType() == typeof(string)))
                                    {
                                        if ((jsonAction.ContainsKey("tcpaddr")) && (jsonAction["tcpaddr"].GetType() == typeof(string)) && (jsonAction.ContainsKey("tcpport")) && ((jsonAction["tcpport"].GetType() == typeof(int)) || (jsonAction["tcpport"].GetType() == typeof(string))))
                                        {
                                            // TCP relay tunnel
                                            try
                                            {
                                                string tcpaddr = (string)jsonAction["tcpaddr"];
                                                int tcpport = 0;
                                                if (jsonAction["tcpport"].GetType() == typeof(int)) { tcpport = (int)jsonAction["tcpport"]; }
                                                if (jsonAction["tcpport"].GetType() == typeof(string)) { tcpport = int.Parse((string)jsonAction["tcpport"]); }
                                                string url = (string)jsonAction["value"];
                                                string hash = null;
                                                if ((jsonAction.ContainsKey("servertlshash")) && (jsonAction["servertlshash"].GetType() == typeof(string))) { hash = (string)jsonAction["servertlshash"]; }
                                                if (url.StartsWith("*/")) { string su = ServerUrl.ToString(); url = su.Substring(0, su.Length - 11) + url.Substring(1); }
                                                Event(userid, string.Format("Started TCP tunnel to {0}:{1}", tcpaddr, tcpport));
                                                MeshCentralTcpTunnel tunnel = new MeshCentralTcpTunnel(this, new Uri(url), hash, jsonAction, tcpaddr, tcpport);
                                                tcptunnels.Add(tunnel);
                                            }
                                            catch (Exception) { }
                                        }
                                        else
                                        {
                                            // Application tunnel
                                            try
                                            {
                                                string url = jsonAction["value"].ToString();
                                                string hash = null;
                                                if ((jsonAction.ContainsKey("servertlshash")) && (jsonAction["servertlshash"].GetType() == typeof(string))) { hash = (string)jsonAction["servertlshash"]; }
                                                if (url.StartsWith("*/")) { string su = ServerUrl.ToString(); url = su.Substring(0, su.Length - 11) + url.Substring(1); }
                                                MeshCentralTunnel tunnel = new MeshCentralTunnel(this, new Uri(url), hash, jsonAction);
                                                tunnels.Add(tunnel);
                                            }
                                            catch (Exception) { }
                                        }
                                    }
                                    break;
                                }
                            case "getclip": {
                                    // Require that the user have an active remote desktop session to perform this operation.
                                    if ((userid == null) || (doesUserHaveSession(userid, 2) == false)) return;
                                    Event(userid, "Requested clipboard content");
                                    GetClipboard(jsonAction);
                                    break;
                                }
                            case "setclip": {
                                    // Require that the user have an active remote desktop session to perform this operation.
                                    if ((userid == null) || (doesUserHaveSession(userid, 2) == false)) return;
                                    Event(userid, "Set clipboard content");
                                    SetClipboard(jsonAction);
                                    break;
                                }
                            case "ps": {
                                    string sessionid = null;
                                    if ((jsonAction.ContainsKey("sessionid")) && (jsonAction["sessionid"].GetType() == typeof(string))) { sessionid = (string)jsonAction["sessionid"]; }
                                    string ps = getAllProcesses();
                                    WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"msg\",\"type\":\"ps\",\"sessionid\":\"" + escapeJsonString(sessionid) + "\",\"value\":\"" + escapeJsonString(ps) + "\"}"));
                                    break;
                                }
                            case "pskill":
                                {
                                    int pid = 0;
                                    if ((jsonAction.ContainsKey("value")) && (jsonAction["value"].GetType() == typeof(System.Int32))) { pid = (int)jsonAction["value"]; }
                                    if (pid > 0) { try { Process p = Process.GetProcessById(pid); if (p != null) { p.Kill(); } } catch (Exception) { } }
                                    Event(userid, string.Format("Killed process {0}", pid));
                                    break;
                                }
                            case "services":
                                {
                                    // TODO: List system services
                                    break;
                                }
                            default:
                                {
                                    Log("Unprocessed event type: " + eventType);
                                    break;
                                }
                        }
                        break;
                    }
                case "meshToolInfo":
                    {
                        string name = null;
                        string hash = null;
                        string url = null;
                        string serverhash = null;
                        if (jsonAction.ContainsKey("name")) { name = jsonAction["name"].ToString(); } // Download tool name
                        if (jsonAction.ContainsKey("hash")) { hash = jsonAction["hash"].ToString(); } // File Hash
                        if (jsonAction.ContainsKey("url")) { url = jsonAction["url"].ToString(); } // Server url
                        if (jsonAction.ContainsKey("serverhash")) { serverhash = jsonAction["serverhash"].ToString(); } // Server TLS certificate hash
                        if ((name != null) && (hash != null) && (url != null) && (onSelfUpdate != null) && (name == softwareName)) { onSelfUpdate(name, hash, url, serverhash); }
                        break;
                    }
                default:
                    {
                        Log("Unprocessed command: " + action);
                        break;
                    }
            }
        }

        private string getAllProcesses()
        {
            Log("getAllProcesses()");
            string r = "{";
            try
            {
                bool first = true;
                Process[] allProcessesOnLocalMachine = Process.GetProcesses();
                foreach (Process process in allProcessesOnLocalMachine)
                {
                    if (process.Id == 0) continue;
                    if (first) { first = false; } else { r += ","; }
                    r += "\"" + process.Id + "\":{\"pid\":" + process.Id + ",\"cmd\":\"" + escapeJsonString(process.ProcessName) + "\"}";
                }
            }
            catch (Exception) { }
            return r + "}";
        }

        private delegate void ClipboardHandler(Dictionary<string, object> jsonAction);

        private void GetClipboard(Dictionary<string, object> jsonAction)
        {
            Log("GetClipboard()");

            // Clipboard can only be fetched from the main thread
            if (parent.InvokeRequired) { parent.Invoke(new ClipboardHandler(GetClipboard), jsonAction); return; }
            if ((jsonAction.ContainsKey("sessionid")) && (jsonAction["sessionid"].GetType() == typeof(string)) && (jsonAction.ContainsKey("tag")) && (jsonAction["tag"].GetType() == typeof(System.Int32)))
            {
                string extraLogStr = "";
                if (jsonAction.ContainsKey("userid") && (jsonAction["userid"].GetType() == typeof(string))) { extraLogStr += ",\"userid\":\"" + escapeJsonString((string)jsonAction["userid"]) + "\""; }
                if (jsonAction.ContainsKey("username") && (jsonAction["username"].GetType() == typeof(string))) { extraLogStr += ",\"username\":\"" + escapeJsonString((string)jsonAction["username"]) + "\""; }
                if (jsonAction.ContainsKey("remoteaddr") && (jsonAction["remoteaddr"].GetType() == typeof(string))) { extraLogStr += ",\"remoteaddr\":\"" + escapeJsonString((string)jsonAction["remoteaddr"]) + "\""; }
                if (jsonAction.ContainsKey("sessionid") && (jsonAction["sessionid"].GetType() == typeof(string))) { extraLogStr += ",\"sessionid\":\"" + escapeJsonString((string)jsonAction["sessionid"]) + "\""; }

                string sessionid = (string)jsonAction["sessionid"];
                int tag = (int)jsonAction["tag"];
                if (Clipboard.ContainsText(TextDataFormat.Text))
                {
                    string clipboardValue = Clipboard.GetText();
                    WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"log\",\"msgid\":21,\"msgArgs\":[" + clipboardValue.Length + "],\"msg\":\"Getting clipboard content, " + clipboardValue.Length + " byte(s)\"" + extraLogStr + "}"));
                    WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"msg\",\"type\":\"getclip\",\"sessionid\":\"" + escapeJsonString(sessionid) + "\",\"data\":\"" + escapeJsonString(clipboardValue) + "\",\"tag\":" + tag + "}"));
                }
            }
        }

        private void SetClipboard(Dictionary<string, object> jsonAction)
        {
            Log("SetClipboard()");

            // Clipboard can only be set from the main thread
            if (parent.InvokeRequired) { parent.Invoke(new ClipboardHandler(SetClipboard), jsonAction); return; }
            if ((jsonAction.ContainsKey("sessionid")) && (jsonAction["sessionid"].GetType() == typeof(string)) && (jsonAction.ContainsKey("data")) && (jsonAction["data"].GetType() == typeof(string)))
            {
                string extraLogStr = "";
                if (jsonAction.ContainsKey("userid") && (jsonAction["userid"].GetType() == typeof(string))) { extraLogStr += ",\"userid\":\"" + escapeJsonString((string)jsonAction["userid"]) + "\""; }
                if (jsonAction.ContainsKey("username") && (jsonAction["username"].GetType() == typeof(string))) { extraLogStr += ",\"username\":\"" + escapeJsonString((string)jsonAction["username"]) + "\""; }
                if (jsonAction.ContainsKey("remoteaddr") && (jsonAction["remoteaddr"].GetType() == typeof(string))) { extraLogStr += ",\"remoteaddr\":\"" + escapeJsonString((string)jsonAction["remoteaddr"]) + "\""; }
                if (jsonAction.ContainsKey("sessionid") && (jsonAction["sessionid"].GetType() == typeof(string))) { extraLogStr += ",\"sessionid\":\"" + escapeJsonString((string)jsonAction["sessionid"]) + "\""; }

                string clipboardData = (string)jsonAction["data"];
                Clipboard.SetText(clipboardData);
                string sessionid = (string)jsonAction["sessionid"];
                WebSocket.SendBinary(UTF8Encoding.UTF8.GetBytes("{\"action\":\"msg\",\"type\":\"setclip\",\"sessionid\":\"" + escapeJsonString(sessionid) + "\",\"success\":true}"));
            }
        }

        static bool ByteArrayCompare(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) { if (a[i] != b[i]) return false; }
            return true;
        }

        private void WebSocket_onStringData(webSocketClient sender, string data, int orglen)
        {
            // Process JSON data
            if ((ConnectionState == 15) && (data.Length > 2) && (data[0] == '{')) { processServerJsonData(data); }
        }

        private void WebSocket_onBinaryData(webSocketClient sender, byte[] data, int off, int len, int orglen)
        {
            if (len < 2) return;

            if ((ConnectionState == 15) && (data[off] == 123))
            {
                // This is JSON data
                processServerJsonData(UTF8Encoding.UTF8.GetString(data, off, len));
                return;
            }

            int cmd = ((data[off] << 8) + data[off + 1]);
            Log(string.Format("Binary command: cmd={0}, len={1}", cmd, len));
            switch (cmd) {
                case 1:
                    {
                        // 0x0001 + TlsHash(48) + ServerNonce(48)
                        if (len != 98) return;
                        byte[] tlsHash = new byte[48];
                        Array.Copy(data, off + 2, tlsHash, 0, 48);
                        ServerNonce = new byte[48];
                        Array.Copy(data, off + 50, ServerNonce, 0, 48);
                        if (ByteArrayCompare(tlsHash, ServerTlsHash) == false) { disconnectex(); return; }

                        // Use our agent root private key to sign the ServerHash + ServerNonce + AgentNonce
                        byte[] dataToSign = new byte[48 + 48 + 48];
                        Array.Copy(tlsHash, 0, dataToSign, 0, 48);
                        Array.Copy(ServerNonce, 0, dataToSign, 48, 48);
                        Array.Copy(Nonce, 0, dataToSign, 96, 48);
                        byte[] signature = agentCert.GetRSAPrivateKey().SignData(dataToSign, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1);

                        // Send back our certificate + signature
                        byte[] certData = agentCert.GetRawCertData();
                        BinaryWriter bw = new BinaryWriter(new MemoryStream());
                        bw.Write(Convert.ToInt16(IPAddress.HostToNetworkOrder((short)2)));
                        bw.Write(Convert.ToInt16(IPAddress.HostToNetworkOrder((short)certData.Length)));
                        bw.Write(certData);
                        bw.Write(signature);
                        WebSocket.SendBinary(((MemoryStream)bw.BaseStream).ToArray());
                        break;
                    }
                case 2:
                    {
                        // 0x0002 + CertLength(2) + AgentCert(CertLength) + signature
                        if (len < 4) return;
                        int certLength = ((data[off + 2] << 8) + data[off + 3]);
                        if (len < certLength + 4) return;
                        byte[] serverCertData = new byte[certLength];
                        Array.Copy(data, off + 4, serverCertData, 0, certLength);
                        byte[] serverSign = new byte[len - 4 - certLength];
                        Array.Copy(data, off + 4 + certLength, serverSign, 0, len - 4 - certLength);

                        // Server signature, verify it
                        X509Certificate2 serverCert = new X509Certificate2(serverCertData);
                        byte[] dataToVerify = new byte[48 + 48 + 48];
                        Array.Copy(ServerTlsHash, 0, dataToVerify, 0, 48);
                        Array.Copy(Nonce, 0, dataToVerify, 48, 48);
                        Array.Copy(ServerNonce, 0, dataToVerify, 96, 48);
                        if (serverCert.GetRSAPublicKey().VerifyData(dataToVerify, serverSign, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1) == false) { disconnectex(); return; }

                        // Connection is a success from our side, clean up
                        ServerTlsHash = null;
                        Nonce = null;
                        ServerNonce = null;
                        ConnectionState |= 4;

                        // Send information about our agent and computer
                        // Command 3: infover, agentid, agentversion, platformtype, meshid, capabilities, computername
                        string hostName = Dns.GetHostName();
                        byte[] hostNameBytes = UTF8Encoding.UTF8.GetBytes(hostName);
                        BinaryWriter bw = new BinaryWriter(new MemoryStream());
                        bw.Write(Convert.ToInt16(IPAddress.HostToNetworkOrder((short)3))); // Command 3
                        bw.Write(Convert.ToInt32(IPAddress.HostToNetworkOrder((int)1))); // Version, always 1
                        bw.Write(Convert.ToInt32(IPAddress.HostToNetworkOrder((int)34))); // Agent ID
                        bw.Write(Convert.ToInt32(IPAddress.HostToNetworkOrder((int)0))); // Agent Version
                        bw.Write(Convert.ToInt32(IPAddress.HostToNetworkOrder((int)1))); // Platform Type, this is the icon: 1 = Desktop, 2 = Laptop, 3 = Mobile, 4 = Server, 5 = Disk, 6 = Router
                        bw.Write(MeshId); // Mesh ID. This is the identifier of the initial device group
                        bw.Write(Convert.ToInt32(IPAddress.HostToNetworkOrder((int)(1 + 4 + 8)))); // Capabilities of the agent (bitmask): 1 = Desktop, 2 = Terminal, 4 = Files, 8 = Console, 16 = JavaScript, 32 = Temporary, 64 = Recovery
                        bw.Write(Convert.ToInt16(IPAddress.HostToNetworkOrder((short)hostNameBytes.Length))); // Computer Name Length
                        bw.Write(hostNameBytes); // Computer name
                        WebSocket.SendBinary(((MemoryStream)bw.BaseStream).ToArray());

                        // If server already confirmed authenticaiton, signal authenticated connection
                        if (ConnectionState == 15) { changeState(3); serverConnected(); }
                        break;
                    }
                case 4:
                    {
                        // Server confirmed authentication, we are allowed to send commands to the server
                        ConnectionState |= 8;
                        if (ConnectionState == 15) { changeState(3); serverConnected(); }
                        break;
                    }
                default:
                    {
                        Log("Unprocessed command: #" + cmd);
                        break;
                    }
            }
        }

    }
}
