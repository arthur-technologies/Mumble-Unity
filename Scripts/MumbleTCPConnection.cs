using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MumbleProto;
using UnityEngine;
using ProtoBuf;
using System.Timers;
using System.Threading;
using System.Text;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Asn1;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Asn1.Pkcs;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Asn1.X509;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Generators;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Parameters;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Crypto.Prng;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Math;
using BestHTTP.SecureProtocol.Org.BouncyCastle.OpenSsl;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Pkcs;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Security;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Utilities;
using BestHTTP.SecureProtocol.Org.BouncyCastle.X509;
using ProtoBuf.Meta;
using Version = MumbleProto.Version;
using X509Certificate = System.Security.Cryptography.X509Certificates.X509Certificate;

namespace Mumble
{
    public class MumbleTcpConnection
    {
        private readonly UpdateOcbServerNonce _updateOcbServerNonce;
        private readonly IPEndPoint _host;
        private readonly string _hostname;
        private readonly MumbleClient _mumbleClient;
        private readonly TcpClient _tcpClient;
        private BinaryReader _reader;
        private SslStream _ssl;
        private MumbleUdpConnection _udpConnection;
        private bool _validConnection;
        private BinaryWriter _writer;
        private System.Timers.Timer _tcpTimer;
        private Thread _processThread;
        private string _username;
        private string _password;

        private TypeModel _myProto;

        internal MumbleTcpConnection(IPEndPoint host, string hostname, UpdateOcbServerNonce updateOcbServerNonce,
            MumbleUdpConnection udpConnection, MumbleClient mumbleClient)
        {
            _host = host;
            _hostname = hostname;
            _mumbleClient = mumbleClient;
            _udpConnection = udpConnection;
            _tcpClient = new TcpClient();
            _updateOcbServerNonce = updateOcbServerNonce;
            
            _processThread = new Thread(ProcessTcpData)
            {
                IsBackground = true
            };
        }

        internal void StartClient(string username, string password)
        {
            _username = username;
            _password = password;
            //_tcpClient.BeginConnect(_host.Address, _host.Port, new AsyncCallback(OnTcpConnected), null);
            //Debug.Log("Attempting to connect to " + _host);
            OnTcpConnected(null);
        }
        private void OnTcpConnected(IAsyncResult connectionResult)
        {
            try
            {
                _tcpClient.Connect(_host);
                if (!_tcpClient.Connected)
                {
                    Debug.LogError("Connection failed! Please confirm that you have internet access, and that the hostname is correct");
                    _mumbleClient?.OnConnectionDisconnect();
                    throw new Exception("Failed to connect");
                }
            } 
            catch (Exception e)
            {
                Debug.LogError("Connection failed! Please confirm that you have internet access, and that the hostname is correct- > Exception caught");
                _mumbleClient?.OnConnectionDisconnect();
                throw;
            }

            if (_myProto == null)
            {
                _myProto = (TypeModel) Activator.CreateInstance(Type.GetType("MyProtoModel, MyProtoModel") ??
                                                               throw new Exception("Failed to Create MyProtoModel Serializer/Deserializer"));
            }

            NetworkStream networkStream = _tcpClient.GetStream();
            _ssl = new SslStream(networkStream, false, ValidateCertificate);//, UserCertificateSelectionCallback);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            //ssl. = SslProtocols.Tls12;
           // var clientCertificate = new X509Certificate2("D:\\MumbleCert\\TGS.pfx","123");
            //var clientCertificateCollection = new X509CertificateCollection(GenerateSelfSignedCertificate());
            //GenerateCACertificate("CN=root ca", (x) => {} );
            /*GenerateSelfSignedCertificate("CN=localhost", "CN=root ca", certificate2 =>
            {
                
            });*/
            try
            {
                _ssl.AuthenticateAsClient(_hostname);//, clientCertificateCollection, SslProtocols.Tls12, false);
                _reader = new BinaryReader(_ssl);
                _writer = new BinaryWriter(_ssl);
                //_ssl.AuthenticateAsClient(_hostname, new X509CertificateCollection(){certificate2}, SslProtocols.Default, false);
            }
            catch (Exception e)
            {
                Debug.LogError("OnTcpConnected-> AuthenticateAsClient");
                //Thread.Sleep(500);
                //_mumbleClient?.OnConnectionDisconnect();
                throw;
            }

            DateTime startWait = DateTime.Now;
            while (!_ssl.IsAuthenticated)
            {
                if (DateTime.Now - startWait > TimeSpan.FromSeconds(2))
                    throw new TimeoutException("Time out waiting for SSL authentication");
                
                Thread.Sleep(10);
            }
            SendVersion();
            //Handshake(_username, _password, null );
            StartPingTimer();
            
        }
        
        private void Handshake(string username, string password, string[] tokens)
        {
            MumbleProto.Version version = new MumbleProto.Version
            {
                Release = MumbleClient.ReleaseName,
                version = (1 << 16) | (2 << 8) | (0 & 0xFF),
                Os = Environment.OSVersion.ToString(),
                OsVersion = Environment.OSVersion.VersionString,
            };
            SendMessage(MessageType.Version, version);

            Authenticate auth = new Authenticate
            {
                Username = username,
                Password = password,
                Opus = true,
            };
            auth.Tokens.AddRange(tokens ?? new string[0]);
            auth.CeltVersions = new int[] { unchecked((int)0x8000000b) };

            SendMessage(MessageType.Authenticate, auth);
        }

        private X509Certificate UserCertificateSelectionCallback(object sender, string targethost, X509CertificateCollection localcertificates, X509Certificate remotecertificate, string[] acceptableissuers)
        {
            X509Certificate2 col = new X509Certificate2(); 
            col.Import(@"D:\MumbleCert\TGS.cer");// Dev Server Path
            //col.Import(@"C:\Users\Shohair\Documents\DEV.p7b");// Local Test
            return col;
            
            /*var caPrivKey = GenerateCACertificate("CN=root ca");
            var cert = GenerateSelfSignedCertificate("CN=localhost", "CN=root ca", caPrivKey);
            return cert;*/
        }

        private void SendVersion()
        {
            var version = new Version
            {
                Release = MumbleClient.ReleaseName,
                version = (MumbleClient.Major << 16) | (MumbleClient.Minor << 8) | (MumbleClient.Patch),
                Os = Environment.OSVersion.ToString(),
                OsVersion = Environment.OSVersion.VersionString,
            };
            SendMessage(MessageType.Version, version);
        }
        private void StartPingTimer()
        {
            // Keepalive, if the Mumble server doesn't get a message 
            // for 30 seconds it will close the connection
            _tcpTimer = new System.Timers.Timer(MumbleConstants.PING_INTERVAL_MS);
            _tcpTimer.Elapsed += SendPing;
            _tcpTimer.Enabled = true; 
            _processThread.Start();
        }

        internal void SendMessage<T>(MessageType mt, T message)
        {
            lock (_ssl)
            {
                try
                {
                    //if (mt != MessageType.Ping && mt != MessageType.UDPTunnel)
                        //Debug.Log("Sending " + mt + " message");
                    //_writer.Write(IPAddress.HostToNetworkOrder((Int16) mt));
                    //Serializer.SerializeWithLengthPrefix(_ssl, message, PrefixStyle.Fixed32BigEndian);
                    Int16 messageType = (Int16)mt;

                    // UDP Tunnels have their own way in which they handle serialization
                    if(mt == MessageType.UDPTunnel)
                    {
                        UDPTunnel udpTunnel = message as UDPTunnel;
                        Int32 messageSize = (Int32)udpTunnel.Packet.Length;
                        _writer.Write(IPAddress.HostToNetworkOrder(messageType));
                        _writer.Write(IPAddress.HostToNetworkOrder(messageSize));
                        _writer.Write(udpTunnel.Packet);
                    }
                    else
                    {
                        MemoryStream messageStream = new MemoryStream();
                        _myProto.Serialize(messageStream,message);
                        //Serializer.NonGeneric.Serialize(messageStream, message);
                        Int32 messageSize = (Int32)messageStream.Length;
                        _writer.Write(IPAddress.HostToNetworkOrder(messageType));
                        _writer.Write(IPAddress.HostToNetworkOrder(messageSize));
                        messageStream.Position = 0;
                        _writer.Write(messageStream.ToArray());
                    }
                    
                    /*
                    StringBuilder sb = new StringBuilder();
                    byte[] msgArray = messageStream.ToArray();
                    for (int i = 0; i < msgArray.Length; i++)
                    {
                        sb.Append(msgArray[i]);
                        sb.Append(",");
                    }
                    Debug.Log(sb.ToString());
                    */
                    _writer.Flush();
                }
                catch (Exception e)
                {
                    Debug.LogError("MumbleTCp-> SendMessage-> " + e);
                    //_mumbleClient.OnConnectionDisconnect();
                    //throw;
                }
            }
        }

        //TODO implement actual certificate validation
        private bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors errors)
        {
            /*if (errors == SslPolicyErrors.None) { return true; }
            if (errors == SslPolicyErrors.RemoteCertificateChainErrors) { return true; } //we don't have a proper certificate tree
            return false;*/
            return true;
        }

        private void ProcessTcpData()
        {
            // This thread is aborted in Close()
            while (true)
            {
                try
                {
                    var messageType = (MessageType)IPAddress.NetworkToHostOrder(_reader.ReadInt16());
                    //Debug.Log("Processing data of type: " + messageType);

                    switch (messageType)
                    {
                        case MessageType.Version:
                            _mumbleClient.RemoteVersion = (Version) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(Version),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Server version: " + _mc.RemoteVersion.release);
                            var authenticate = new Authenticate
                            {
                                Username = _username,
                                Password = _password,
                                Opus = true
                            };
                            SendMessage(MessageType.Authenticate, authenticate);
                            break;
                        case MessageType.CryptSetup:
                            var cryptSetup = (CryptSetup) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(CryptSetup),
                                PrefixStyle.Fixed32BigEndian, 0);
                            ProcessCryptSetup(cryptSetup);
                            //Debug.Log("Got crypt");
                            break;
                        case MessageType.CodecVersion:
                            _mumbleClient.CodecVersion = (CodecVersion) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(CodecVersion),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Got codec version");
                            break;
                        case MessageType.ChannelState:
                            ChannelState ChannelState = (ChannelState) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(ChannelState),
                                PrefixStyle.Fixed32BigEndian, 0);
                            /*
                            Debug.Log("Channel state Name = " + ChannelState.name);
                            Debug.Log("Channel state ID = " + ChannelState.channel_id);
                            Debug.Log("Channel state Position = " + ChannelState.position);
                            Debug.Log("Channel state Temporary = " + ChannelState.temporary);
                            Debug.Log("Channel state Parent = " + ChannelState.parent);
                            Debug.Log("Channel state Description = " + ChannelState.description);
                            */
                            _mumbleClient.AddChannel(ChannelState);
                            break;
                        case MessageType.PermissionQuery:
                            _mumbleClient.PermissionQuery = (PermissionQuery) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(PermissionQuery),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Permission Query = " + _mumbleClient.PermissionQuery.permissions);
                            //Debug.Log("Permission Channel = " + _mumbleClient.PermissionQuery.channel_id);
                            break;
                        case MessageType.UserState:
                            //This is called for every user in the room, including us
                            UserState user = (UserState) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(UserState),
                                PrefixStyle.Fixed32BigEndian, 0);

                            //Debug.Log("Name: " + user.Name);
                            //Debug.Log("Session: " + user.Session);
                            //Debug.Log("actor: " + user.Actor);
                            //Debug.Log("Chan: " + user.ChannelId);
                            //Debug.Log("ID: " + user.UserId);
                            _mumbleClient.AddOrUpdateUser(user);
                            break;
                        case MessageType.ServerSync:
                            //This is where we get our session Id
                            //Debug.Log("Will server sync!");
                            _mumbleClient.SetServerSync((ServerSync) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(ServerSync),
                                PrefixStyle.Fixed32BigEndian, 0));
                            //Debug.Log("Server Sync Session= " + _mumbleClient.ServerSync.session);
                            break;
                        case MessageType.ServerConfig:
                            _mumbleClient.ServerConfig = (ServerConfig) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(ServerConfig),
                                PrefixStyle.Fixed32BigEndian, 0);
                            //Debug.Log("Sever config = " + _mumbleClient.ServerConfig);
                            Debug.Log("Mumble is Connected");
                            _mumbleClient.OnConnectionConnected();
                            _validConnection = true; // handshake complete
                            break;
                        case MessageType.SuggestConfig:
                            //Contains suggested configuratio options from the server
                            //like whether to send positional data, client version, etc.
                            _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(SuggestConfig),
                                PrefixStyle.Fixed32BigEndian, 0);
                            break;
                        case MessageType.TextMessage:
                            TextMessage textMessage = (TextMessage) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(TextMessage),
                                PrefixStyle.Fixed32BigEndian, 0);

                            Debug.Log("Text message = " + textMessage.Message);
                            Debug.Log("Text actor = " + textMessage.Actor);
                            //Debug.Log("Text channel = " + textMessage.channel_id[0]);
                            //Debug.Log("Text session Length = " + textMessage.Sessions.Length);
                            //Debug.Log("Text Tree Length = " + textMessage.TreeIds.Length);
                            break;
                        case MessageType.UDPTunnel:
                            var length = IPAddress.NetworkToHostOrder(_reader.ReadInt32());
                            //Debug.Log("Received UDP tunnel of length: " + length);
                            //At this point the message is already decrypted
                            _udpConnection.UnpackOpusVoicePacket(_reader.ReadBytes(length), false);
                            /*
                            //var udpTunnel = Serializer.DeserializeWithLengthPrefix<UDPTunnel>(_ssl,
                                PrefixStyle.Fixed32BigEndian);
                            */
                            break;
                        case MessageType.Ping:
                            _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(MumbleProto.Ping),
                                PrefixStyle.Fixed32BigEndian, 0);
                            break;
                        case MessageType.Reject:
                            // This is called, for example, when the max number of users has been hit
                            var reject = (Reject) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(Reject),
                                PrefixStyle.Fixed32BigEndian, 0);
                            _validConnection = false;
                            Debug.LogError("Mumble server reject: " + reject.Reason);
                            _mumbleClient.OnConnectionDisconnect();
                            // The server connection is over, so we return
                            return;
                        case MessageType.UserRemove:
                            var removal = (UserRemove) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(UserRemove),
                                PrefixStyle.Fixed32BigEndian, 0);
                            Debug.Log("Removing " + removal.Session);
                            _mumbleClient.RemoveUser(removal.Session);
                            break;
                        case MessageType.ChannelRemove:
                            var removedChan = (ChannelRemove) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(ChannelRemove),
                                PrefixStyle.Fixed32BigEndian, 0);
                            _mumbleClient.RemoveChannel(removedChan.ChannelId);
                            Debug.Log("Removing channel " + removedChan.ChannelId);
                            break;
                        case MessageType.PermissionDenied:
                            var denial = (PermissionDenied) _myProto.DeserializeWithLengthPrefix(_ssl, null, typeof(PermissionDenied),
                                PrefixStyle.Fixed32BigEndian, 0);
                            Debug.LogError("Permission denied with fields Name: " + denial.Name + ", Type: " + denial.Type + ", Reason: " + denial.Reason);
                            break;
                        default:
                            Debug.LogError("Message type " + messageType + " not implemented");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    if (ex is EndOfStreamException)
                    {
                        Debug.LogError("EOS Exception(Same Username): " + ex);//This happens when we connect again with the same username
                        _mumbleClient.OnConnectionDisconnect();
                    }
                    else if (ex is IOException)
                    {
                        Debug.LogError("IO Exception: " + ex);
                        _mumbleClient.OnConnectionDisconnect();
                    }
                    //These just means the app stopped, it's ok
                    else if (ex is ObjectDisposedException)
                    {
                        Debug.LogError("ObjectDisposedException error: " + ex);
                        _mumbleClient.OnConnectionDisconnect();
                    }
                    else if (ex is ThreadAbortException)
                    {
                        //Debug.LogError("ThreadAbortException error: " + ex);
                        _mumbleClient.OnConnectionDisconnect();
                    }
                    else
                    {
                        Debug.LogError("Unhandled error: " + ex);
                        _mumbleClient.OnConnectionDisconnect();
                    }

                    break;
                }
            }
        }

        private void ProcessCryptSetup(CryptSetup cryptSetup)
        {
            if (cryptSetup.Key != null && cryptSetup.ClientNonce != null && cryptSetup.ServerNonce != null)
            {
                // Apply the key and client/server nonce values provided
                _mumbleClient.CryptSetup = cryptSetup;
                _mumbleClient.ConnectUdp();
            }
            else if(cryptSetup.ServerNonce != null)
            {
                Debug.Log("Updating server nonce");
                _updateOcbServerNonce(cryptSetup.ServerNonce);
            }
            else
            {
                // This generally means that the server is requesting our nonce
                SendMessage(MessageType.CryptSetup, new CryptSetup { ClientNonce = _mumbleClient.GetLatestClientNonce() });
            }
        }

        internal void Close()
        {
            if(_ssl != null)
                _ssl.Close();
            _ssl = null;
            if(_tcpTimer != null)
                _tcpTimer.Close();
            _tcpTimer = null;
            if(_processThread != null)
                _processThread.Abort();
            _processThread = null;
            if(_reader != null)
                _reader.Close();
            _reader = null;
            if(_writer != null)
                _writer.Close();
            _writer = null;
            if(_tcpClient != null)
                _tcpClient.Close();
        }

        internal void SendPing(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            if (_validConnection)
            {
                var ping = new MumbleProto.Ping();
                ping.Timestamp = (ulong) (DateTime.UtcNow.Ticks - DateTime.Parse("01/01/1970 00:00:00").Ticks);
                //Debug.Log("Sending ping");
                SendMessage(MessageType.Ping, new MumbleProto.Ping());
            }
        }

        #region Certificate Generation

        public static void GenerateSelfSignedCertificate(string subjectName, string issuerName, Action<X509Certificate2> callback,  int keyStrength = 2048)
{
    // Generating Random Numbers
    var randomGenerator = new CryptoApiRandomGenerator();
    var random = new SecureRandom(randomGenerator);

    // The Certificate Generator
    var certificateGenerator = new X509V3CertificateGenerator();

    // Serial Number
    var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
    certificateGenerator.SetSerialNumber(serialNumber);

    // Signature Algorithm
    const string signatureAlgorithm = "SHA256WithRSA";
    certificateGenerator.SetSignatureAlgorithm(signatureAlgorithm);

    // Issuer and Subject Name
    var subjectDN = new X509Name(subjectName);
    var issuerDN = new X509Name(issuerName);
    certificateGenerator.SetIssuerDN(issuerDN);
    certificateGenerator.SetSubjectDN(subjectDN);

    // Valid For
    var notBefore = DateTime.UtcNow.Date;
    var notAfter = notBefore.AddYears(2);

    certificateGenerator.SetNotBefore(notBefore);
    certificateGenerator.SetNotAfter(notAfter);

    // Subject Public Key
    AsymmetricCipherKeyPair subjectKeyPair;
    var keyGenerationParameters = new KeyGenerationParameters(random, keyStrength);
    var keyPairGenerator = new RsaKeyPairGenerator();
    keyPairGenerator.Init(keyGenerationParameters);
    subjectKeyPair = keyPairGenerator.GenerateKeyPair();

    certificateGenerator.SetPublicKey(subjectKeyPair.Public);

    // Generating the Certificate
    var issuerKeyPair = subjectKeyPair;

    // Selfsign certificate
    var certificate = certificateGenerator.Generate(issuerKeyPair.Private, random);

    // Corresponding private key
    PrivateKeyInfo info = PrivateKeyInfoFactory.CreatePrivateKeyInfo(subjectKeyPair.Private);


    // Merge into X509Certificate2
    var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate.GetEncoded());

    var seq = (Asn1Sequence)Asn1Object.FromByteArray(info.ParsePrivateKey().GetDerEncoded());
    if (seq.Count != 9)
        throw new PemException("malformed sequence in RSA private key");

    var rsa = new RsaPrivateKeyStructure(seq);
    RsaPrivateCrtKeyParameters rsaparams = new RsaPrivateCrtKeyParameters(
        rsa.Modulus, rsa.PublicExponent, rsa.PrivateExponent, rsa.Prime1, rsa.Prime2, rsa.Exponent1, rsa.Exponent2, rsa.Coefficient);

    x509.PrivateKey = DotNetUtilities.ToRSA(rsaparams);
    callback(x509);
}


public static void X509CertifienerateCACertificate(string subjectName, Action<X509Certificate2> callback, int keyStrength = 2048 )
{
    // Generating Random Numbers
    var randomGenerator = new CryptoApiRandomGenerator();
    var random = new SecureRandom(randomGenerator);

    // The Certificate Generator
    var certificateGenerator = new X509V3CertificateGenerator();

    // Serial Number
    var serialNumber = BigIntegers.CreateRandomInRange(BigInteger.One, BigInteger.ValueOf(Int64.MaxValue), random);
    certificateGenerator.SetSerialNumber(serialNumber);

    // Signature Algorithm
    const string signatureAlgorithm = "SHA256WithRSA";
    certificateGenerator.SetSignatureAlgorithm(signatureAlgorithm);

    // Issuer and Subject Name
    var subjectDN = new X509Name(subjectName);
    var issuerDN = subjectDN;
    certificateGenerator.SetIssuerDN(issuerDN);
    certificateGenerator.SetSubjectDN(subjectDN);

    // Valid For
    var notBefore = DateTime.UtcNow.Date;
    var notAfter = notBefore.AddYears(2);

    certificateGenerator.SetNotBefore(notBefore);
    certificateGenerator.SetNotAfter(notAfter);

    // Subject Public Key
    AsymmetricCipherKeyPair subjectKeyPair;
    var keyGenerationParameters = new KeyGenerationParameters(random, keyStrength);
    var keyPairGenerator = new RsaKeyPairGenerator();
    keyPairGenerator.Init(keyGenerationParameters);
    subjectKeyPair = keyPairGenerator.GenerateKeyPair();

    certificateGenerator.SetPublicKey(subjectKeyPair.Public);

    // Generating the Certificate
    var issuerKeyPair = subjectKeyPair;

    // Selfsign certificate
    var certificate = certificateGenerator.Generate(issuerKeyPair.Private, random);
    var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate.GetEncoded());
    
    x509.PrivateKey = AsymmetricAlgorithm.Create();

    // Add CA certificate to Root store
    //addCertToStore(cert, StoreName.Root, StoreLocation.CurrentUser);
    callback(x509);
    //return issuerKeyPair.Private;
}

        #endregion
        
    }
}