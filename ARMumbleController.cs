/*
 * This is the front facing script to control how MumbleUnity works.
 * It's expected that, to fit in properly with your application,
 * You'll want to change this class (and possible SendMumbleAudio)
 * in order to work the way you want it to
 */
using UnityEngine;
using System;
#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Arthur.Client.Controllers;
using Arthur.Common.Utilities;
using MEC;
using ModestTree;
using Mumble;

public class ARMumbleController : MonoBehaviour {

    // Basic mumble audio player
    public GameObject MyMumbleAudioPlayerPrefab;
    // Mumble audio player that also receives position commands
    public GameObject MyMumbleAudioPlayerPositionedPrefab;

    public MumbleMicrophone MyMumbleMic;
    public DebugValues DebuggingVariables;

    public MumbleClient _mumbleClient;
    public bool ConnectAsyncronously = true;
    public bool SendPosition = false;
    public string HostName = "1.2.3.4";
    public int Port = 64738;
    public string Username = "ExampleUser";
    public string Password = "1passwordHere!";
    public string ChannelToJoin = "";
    
    private Exception _updateLoopThreadException = null;

    private State _clientState = State.Disconnected;

    private enum State
    {
        Connecting,
        Connected,
        Disconnected,
        Reconnecting
    }

	void Start () {

        if(HostName == "1.2.3.4")
        {
            Debug.LogError("Please set the mumble host name to your mumble server");
            return;
        }
        Application.runInBackground = true;
        HostName = CommonConfigurations.MumbleUrl;
        Port = CommonConfigurations.MumblePort;
        Password = CommonConfigurations.MumblePassword;
        // If SendPosition, we'll send three floats.
        // This is roughly the standard for Mumble, however it seems that
        // Murmur supports more
        int posLength = SendPosition ? 3 * sizeof(float) : 0;
        _mumbleClient = new MumbleClient(HostName, Port, CreateMumbleAudioPlayerFromPrefab,
            DestroyMumbleAudioPlayer, OnOtherUserStateChange, ConnectAsyncronously,
            SpeakerCreationMode.IN_ROOM_NO_MUTE, DebuggingVariables, posLength)
        {
            OnDisconnected = OnDisconnected,
            OnConnected = OnConnected
        };
        
        if (DebuggingVariables.UseRandomUsername)
            Username += UnityEngine.Random.Range(0, 100f);

        if (ConnectAsyncronously)
        {
            //StartCoroutine(ConnectAsync());
        }
        else
        {
            _mumbleClient.Connect(Username, Password, b =>
            {
                if (b)
                {
                    StartUpdateLoop();
                }
                else
                {
                    Debug.LogError("MumbleCline: TCP cannot connect");
                }
            });
            if(MyMumbleMic != null)
            {
                StartCoroutine(_mumbleClient.AddMumbleMic(MyMumbleMic));
                if (SendPosition)
                    MyMumbleMic.SetPositionalDataFunction(WritePositionalData);
            }
        }

#if UNITY_EDITOR
        if (DebuggingVariables.EnableEditorIOGraph)
        {
            EditorGraph editorGraph = EditorWindow.GetWindow<EditorGraph>();
            editorGraph.Show();
            StartCoroutine(UpdateEditorGraph());
        }
#endif
    }

    public IEnumerator<float> Reconnect()
    {
        _mumbleClient.OnConnectionDisconnect();
        yield return Timing.WaitForOneFrame;
        //yield return Timing.WaitForSeconds(2);
        //Timing.RunCoroutine(ConnectAsync());
    }

    public void StartUpdateLoop()
    {
        ThreadStart updateLoopThreadStart = new ThreadStart(() => UpdateLoop(_mumbleClient, out _updateLoopThreadException));
        updateLoopThreadStart += () => {
            if (_updateLoopThreadException != null)
            {
                EventProcessor.Instance.QueueEvent(() =>
                {
                    ArNotificationManager.Instance.ShowAudioReconnectingPopup();
                });
                _mumbleClient.OnConnectionDisconnect();
                Thread.Sleep(2000);
                if (isAppClosing)
                    return;
                EventProcessor.Instance.QueueEvent(() =>
                {
                    speakerCount++;
                    Timing.RunCoroutine(ConnectAsync());
                });
                throw new Exception($"{nameof(UpdateLoop)} was terminated unexpectedly because of a {_updateLoopThreadException.GetType().ToString()}", _updateLoopThreadException);
            }
        };
        
        Thread updateLoopThread = new Thread(updateLoopThreadStart) {IsBackground = true};
        updateLoopThread.Start();
    }
    private void UpdateLoop(MumbleClient mumbleClient,out Exception exception)
    {
        exception = null;
        try
        {
            while (_clientState != State.Disconnected)
            {
                if (mumbleClient.Process())
                    Thread.Yield();
                else
                    Thread.Sleep(10);
            }

            Debug.Log("ClientState Disconnected, Ending UpdateLoop");
        } catch (Exception ex)
        {
            exception = ex;
            Debug.LogError("UpdateLoop: Exception: " + ex.Message);
        }
    }
    private void OnConnected()
    {
        Debug.LogError("OnConnected: : : ");
        ArNotificationManager.Instance.HideAudioReconnectingPopup();
        //isJoinedChannel = false;
        _clientState = State.Connected;
        StartCoroutine( JoinChannel(ChannelToJoin));
        StartMicrophone();

        StartCoroutine(RefreshUserState());
    }

    private void OnDisconnected()
    {
        isJoinedChannel = false;
        _clientState = State.Disconnected;

        if (!isAppClosing)
        {
            _clientState = State.Reconnecting;
            // StartCoroutine(ConnectAsync());
            Debug.Log("Reconnecting Mumble!");
        }
        else
        {
            Debug.Log("AppClosing, No Reconnect Mumble!");
        }
    }

    private IEnumerator RefreshUserState()
    {
        if (!_mumbleClient.IsSelfMuted())
        {
            _mumbleClient.SetSelfMute(true);
            yield return new WaitForSeconds(0.5f);
            _mumbleClient.SetSelfMute(false);
        }
    }

    /// <summary>
    /// An example of how to serialize the positional data that you're interested in
    /// NOTE: this function, in the current implementation, is called regardless
    /// of if the user is speaking
    /// </summary>
    /// <param name="posData"></param>
    private void WritePositionalData(ref byte[] posData, ref int posDataLength)
    {
        // Get the XYZ position of the camera
        Vector3 pos = Camera.main.transform.position;
        //Debug.Log("Sending pos: " + pos);
        // Copy the XYZ floats into our positional array
        int dstOffset = 0;
        Buffer.BlockCopy(BitConverter.GetBytes(pos.x), 0, posData, dstOffset, sizeof(float));
        dstOffset += sizeof(float);
        Buffer.BlockCopy(BitConverter.GetBytes(pos.y), 0, posData, dstOffset, sizeof(float));
        dstOffset += sizeof(float);
        Buffer.BlockCopy(BitConverter.GetBytes(pos.z), 0, posData, dstOffset, sizeof(float));

        posDataLength = 3 * sizeof(float);
        // The reverse method is in MumbleExamplePositionDisplay
    }

    private bool isJoinedChannel = false;
    public bool IsConnected => _clientState == State.Connected;

    private bool isAppClosing = false;

    private int speakerCount = 0;
    public IEnumerator<float> ConnectAsync()
    {
        switch (_clientState)
        {
            case State.Connecting:
                Debug.Log("Already in Connecting State--> mumble");
                yield return Timing.WaitForOneFrame;
                break;
            case State.Connected:
                string currentChannel;
                try
                {
                    currentChannel = _mumbleClient.GetCurrentChannel();
                }
                catch (Exception e)
                {
                    Debug.LogError("Mumble Coonected GetCurrentChannel Exception: ");
                    throw;
                }

                if (_mumbleClient != null && !currentChannel.Equals(ChannelToJoin))
                {
                    isJoinedChannel = false;
                    yield return Timing.WaitUntilDone(JoinChannel(ChannelToJoin));
                    
                    yield return Timing.WaitForSeconds(1);
                    if (!_mumbleClient.IsSelfMuted())
                    {
                        _mumbleClient.SetSelfMute(true);
                        _mumbleClient.SetSelfMute(false);
                    }
                }
                else
                {
                    Debug.LogError("Channel Already Joined");
                    yield return Timing.WaitForSeconds(1);
                    if (!_mumbleClient.IsSelfMuted())
                    {
                        _mumbleClient.SetSelfMute(true);
                        _mumbleClient.SetSelfMute(false);
                    }
                }
                
                break;
            case State.Disconnected:
                if (_mumbleClient != null)
                {
                    _clientState = State.Connecting;
                    while (!_mumbleClient.ReadyToConnect)
                        yield return Timing.WaitForOneFrame;
                    Debug.Log("Will now connect");
                    //yield return new WaitForEndOfFrame();
                    Username = Username.Split('-')[0] + "-" +speakerCount;
                    _mumbleClient.Connect(Username, Password, b =>
                    {
                        if (b)
                        {
                            StartUpdateLoop();
                        }
                        else
                        {
                            StartUpdateLoop();
                            Debug.LogError("MumbleClient: TCP Disconnected");
                        }
                    } );
                }
                else
                {
                    Debug.Log("MumbleCLient Not Initialized yet");
                }

                break;
            case State.Reconnecting:
                //yield return new WaitForSeconds(2.0f);
                if (_mumbleClient != null)
                {
                    _clientState = State.Connecting;
                    while (!_mumbleClient.ReadyToConnect)
                        yield return Timing.WaitForOneFrame;
                    Debug.Log("Will now Reconnect");
                    //yield return new WaitForEndOfFrame();
                    Username = Username.Split('-')[0] + "-" +speakerCount;
                    _mumbleClient.Connect(Username, Password, b =>
                    {
                        if (b)
                        {
                            StartUpdateLoop();
                        }
                        else
                        {
                            StartUpdateLoop();
                            Debug.LogError("MumbleClient: TCP Disconnected");
                        }
                    } );
                    
                }
                else
                {
                    Debug.Log("MumbleCLient Not Initialized yet");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        /*if (_clientState == State.Connected)
        {
            isJoinedChannel = false;
            yield return JoinChannel(ChannelToJoin);
            yield break;
        }
        yield return new WaitForSeconds(1.0f);
        while (!_mumbleClient.ReadyToConnect)
            yield return null;
        Debug.Log("Will now connect");
        //isConnected = true;
        yield return new WaitForEndOfFrame();
        _mumbleClient.Connect(Username, Password);*/
       // yield return JoinChannel(ChannelToJoin);
        // isJoinedChannel = _mumbleClient.JoinChannel(ChannelToJoin);
        //if (!MyMumbleMic.isRecording)
        //{
        //    StartMicrophone();
        //}
    }

    public void StartMicrophone()
    {
        if(MyMumbleMic != null && MeetingController.instance.playerController.IsMicrophoneAccessGranted)
        {
            StartCoroutine(_mumbleClient.AddMumbleMic(MyMumbleMic));
            if (SendPosition)
                MyMumbleMic.SetPositionalDataFunction(WritePositionalData);
            MyMumbleMic.OnMicDisconnect += OnMicDisconnected;
        }
        else
        {
            Debug.LogError("Microphone Access not Granted, trying to Start Mic!");
        }
    }

    IEnumerator<float> JoinChannel(string channel)
    {
        while (!isJoinedChannel)
        {
            isJoinedChannel = _mumbleClient.JoinChannel(channel);   
            yield return Timing.WaitForSeconds(1);
        }
    }
    
    private MumbleAudioPlayer CreateMumbleAudioPlayerFromPrefab(string username, uint session)
    {
        // Depending on your use case, you might want to add the prefab to an existing object (like someone's head)
        // If you have users entering and leaving frequently, you might want to implement an object pool
        GameObject newObj = SendPosition
            ? GameObject.Instantiate(MyMumbleAudioPlayerPositionedPrefab)
            : GameObject.Instantiate(MyMumbleAudioPlayerPrefab);

        newObj.name = username + "_MumbleAudioPlayer";
        MumbleAudioPlayer newPlayer = newObj.GetComponent<MumbleAudioPlayer>();
        Debug.Log("Adding audio player for: " + username);
        return newPlayer;
    }
    private void OnOtherUserStateChange(uint session, MumbleProto.UserState updatedDeltaState, MumbleProto.UserState fullUserState)
    {
        Debug.Log("User #" + session + " had their user state change: " );
        // Here we can do stuff like update a UI with users' current channel/mute etc.
    }
    private void DestroyMumbleAudioPlayer(uint session, MumbleAudioPlayer playerToDestroy)
    {
        if (playerToDestroy != null)
        {
            Destroy(playerToDestroy.gameObject);
        }
    }
    private void OnMicDisconnected()
    {
        Debug.LogError("Connected microphone has disconnected!");
        string disconnectedMicName = MyMumbleMic.GetCurrentMicName();
        // This means that the mic that we were previously receiving audio from has disconnected
        // you may want to present a notification to the user, allowing them to select
        // a new mic to use
        // here, we will start a coroutine to wait until the mic we want is connected again
        StartCoroutine(ExampleMicReconnect(disconnectedMicName));
    }
    IEnumerator ExampleMicReconnect(string micToConnect)
    {
        while (true)
        {
            string[] micNames = Microphone.devices;
            // try to see if the desired mic is connected
            for(int i = 0; i < micNames.Length; i++)
            {
                if(micNames[i] == micToConnect)
                {
                    Debug.Log("Desired mic reconnected");
                    MyMumbleMic.MicNumberToUse = i;
                    MyMumbleMic.StartSendingAudio(_mumbleClient.EncoderSampleRate);
                    yield break;
                }
            }
            yield return new WaitForSeconds(2f);
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            //_mumbleClient.Close();
        }
        else
        {
            if (MeetingController.instance.currentMeeting != null)
            {
                // StartCoroutine(ConnectAsync());
            }
        }
    }

    void OnApplicationQuit()
    {
        Debug.LogWarning("Shutting down connections");
        isAppClosing = true;
        _mumbleClient?.Close();
    }
    IEnumerator UpdateEditorGraph()
    {
        long numPacketsReceived = 0;
        long numPacketsSent = 0;
        long numPacketsLost = 0;

        while (true)
        {
            yield return new WaitForSeconds(0.1f);

            long numSentThisSample = _mumbleClient.NumUDPPacketsSent - numPacketsSent;
            long numRecvThisSample = _mumbleClient.NumUDPPacketsReceieved - numPacketsReceived;
            long numLostThisSample = _mumbleClient.NumUDPPacketsLost - numPacketsLost;

            Graph.channel[0].Feed(-numSentThisSample);//gray
            Graph.channel[1].Feed(-numRecvThisSample);//blue
            Graph.channel[2].Feed(-numLostThisSample);//red

            numPacketsSent += numSentThisSample;
            numPacketsReceived += numRecvThisSample;
            numPacketsLost += numLostThisSample;
        }
    }
	void Update () {
        
        /*
        if (!_mumbleClient.ReadyToConnect)
            return;
        if (Input.GetKeyDown(KeyCode.S))
        {
            _mumbleClient.SendTextMessage("This is an example message from Unity");
            print("Sent mumble message");
        }
        if (Input.GetKeyDown(KeyCode.J))
        {
            print("Will attempt to join channel " + ChannelToJoin);
            _mumbleClient.JoinChannel(ChannelToJoin);
        }
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            print("Will join root");
            _mumbleClient.JoinChannel("Root");
        }
        if (Input.GetKeyDown(KeyCode.C))
        {
            print("Will set our comment");
            _mumbleClient.SetOurComment("Example Comment");
        }
        if (Input.GetKeyDown(KeyCode.B))
        {
            print("Will set our texture");
            byte[] commentHash = new byte[] { 1, 2, 3, 4, 5, 6 };
            _mumbleClient.SetOurTexture(commentHash);
        }

        // You can use the up / down arrows to increase/decrease
        // the bandwidth used by the mumble mic
        const int BandwidthChange = 5000;
        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            int currentBW = MyMumbleMic.GetBitrate();
            int newBitrate = currentBW + BandwidthChange;
            Debug.Log("Increasing bitrate " + currentBW + "->" + newBitrate);
            MyMumbleMic.SetBitrate(newBitrate);
        }
        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            int currentBW = MyMumbleMic.GetBitrate();
            int newBitrate = currentBW - BandwidthChange;
            Debug.Log("Decreasing bitrate " + currentBW + "->" + newBitrate);
            MyMumbleMic.SetBitrate(newBitrate);
        }
        */
        
    }
}
