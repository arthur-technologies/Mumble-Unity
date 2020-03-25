﻿using UnityEngine;
using System.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using Arthur.Client.Controllers;
using MumbleProto;
using Sirenix.Utilities;

namespace Mumble
{
    [RequireComponent(typeof(AudioSource))]
    public class MumbleAudioPlayer : MonoBehaviour
    {

        public float Gain = 1;
        public string UserName;
        public string UserId;
        public UInt32 Session { get; private set; }
        /// <summary>
        /// Notification that a new audio sample is available for processing
        /// It will be called on the audio thread
        /// It will contain the audio data, which you may want to process in
        /// your own code, and it contains the percent of the data left
        /// un-read
        /// </summary>
        public Action<float[], float> OnAudioSample;

        private MumbleClient _mumbleClient;
        private AudioSource _audioSource;
        private bool _isPlaying = false;
        private float _pendingAudioVolume = -1f;

        void Start()
        {
            _audioSource = GetComponent<AudioSource>();
            // In editor, double check that "auto-play" is turned off
#if UNITY_EDITOR
            if (_audioSource.playOnAwake)
                Debug.LogWarning("For best performance, please turn \"Play On Awake\" off");
#endif
            // In principle, this line shouldn't need to be here.
            // however, from profiling it seems that Unity will
            // call OnAudioFilterRead when the audioSource hits
            // Awake, even if PlayOnAwake is off
            _audioSource.Stop();

            if (_pendingAudioVolume >= 0)
                _audioSource.volume = _pendingAudioVolume;
            _pendingAudioVolume = -1f;
            
            this.bufferSamples = 20 * 48000 / 1000 + 1920 + 48000;
        }
        public string GetUsername()
        {
            if (_mumbleClient == null)
                return null;
            UserState state = _mumbleClient.GetUserFromSession(Session);
            if (state == null)
                return null;
            return state.Name;
        }
        public string GetUserComment()
        {
            if (_mumbleClient == null)
                return null;
            UserState state = _mumbleClient.GetUserFromSession(Session);
            if (state == null)
                return null;
            return state.Comment;
        }
        public byte[] GetUserTexture()
        {
            if (_mumbleClient == null)
                return null;
            UserState state = _mumbleClient.GetUserFromSession(Session);
            if (state == null)
                return null;
            return state.Texture;
        }
        public void Initialize(MumbleClient mumbleClient, UInt32 session)
        {
            Debug.Log("Initialized " + session, this);
            Session = session;
            _mumbleClient = mumbleClient;
            //_mumbleClient.OnRecvAudioDecodedThreaded = OnRecvAudioDecodedThreaded;

            string userStateName = GetUsername();
            UserId = userStateName.Split('_')[0];
            UserName = userStateName.Split('_')[1];

            StartCoroutine(AssignToMemeberPrefab());
        }

        IEnumerator AssignToMemeberPrefab()
        {
            while (true)
            {
                var memberItem = MeetingController.instance.GetMemberByUserId(UserId);

                if (memberItem.SafeIsUnityNull())
                {
                    yield return new WaitForSeconds(1);
                }
                else
                {
                    Transform transform1;
                    (transform1 = this.transform).SetParent(memberItem.transform);
                    transform1.localPosition = Vector3.zero;
                    transform1.localRotation = Quaternion.identity;
                    yield break;
                }
            }
        }

        private static Queue<Tuple<float[], int>> frameData = new Queue<Tuple<float[], int>>();
        
        private void OnRecvAudioDecodedThreaded(float[] data, int samples)
        {
            //Debug.Log("OnRecvAudioDecodedThreaded = " +data.Length + " Samples: " + samples);
            //frameData.Enqueue(new Tuple<float[], int>(data, samples));
            //this._audioSource.clip.SetData(data, this.streamSamplePos % samples);
            //this.streamSamplePos += data.Length / 2;
        }

        public void Reset()
        {
            _mumbleClient = null;
            Session = 0;
            OnAudioSample = null;
            _isPlaying = false;
            if (_audioSource != null)
                _audioSource.Stop();
            _pendingAudioVolume = -1f;
        }
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (_mumbleClient == null || !_mumbleClient.ConnectionSetupFinished)
                return;
            //Debug.Log("Filter read for: " + GetUsername());

            int numRead = _mumbleClient.LoadArrayWithVoiceData(Session, data, 0, data.Length);
            float percentUnderrun = 1f - numRead / data.Length;

            if (OnAudioSample != null)
                OnAudioSample(data, percentUnderrun);

            //Debug.Log("playing audio with avg: " + data.Average() + " and max " + data.Max());
            if (Gain == 1)
                return;

            for (int i = 0; i < data.Length; i++)
                data[i] = Mathf.Clamp(data[i] * Gain, -1f, 1f);
            //Debug.Log("playing audio with avg: " + data.Average() + " and max " + data.Max());
        }
        public bool GetPositionData(out byte[] positionA, out byte[] positionB, out float distanceAB)
        {
            if (!_isPlaying)
            {
                positionA = null;
                positionB = null;
                distanceAB = 0;
                return false;
            }
            double prevPosTime;
            bool ret = _mumbleClient.LoadArraysWithPositions(Session, out positionA, out positionB, out prevPosTime);

            // Get the percent from posA->posB based on the dsp time
            distanceAB = (float)((AudioSettings.dspTime - prevPosTime) / (1000.0 * MumbleConstants.FRAME_SIZE_MS));

            return ret;
        }
        public void SetVolume(float volume)
        {
            if (_audioSource == null)
                _pendingAudioVolume = volume;
            else
                _audioSource.volume = volume;
        }
        
        private int streamSamplePos = 0;
        private int bufferSamples = 0;
        float[] data = new float[1024];
        void Update()
        {
            if (_mumbleClient == null)
                return;
            if (!_isPlaying && _mumbleClient.HasPlayableAudio(Session))
            {
                _isPlaying = true;
                #if UNITY_EDITOR
                this._audioSource.loop = true;
                // using streaming clip leads to too long delays
                this._audioSource.clip = AudioClip.Create("AudioStreamPlayer", bufferSamples, 2, 48000, false);
                #endif
                _audioSource.Play();
                
                
                
                Debug.Log("Playing audio for: " + GetUsername());
            }
            else if (_isPlaying && !_mumbleClient.HasPlayableAudio(Session))
            {
                _audioSource.Stop();
                _isPlaying = false;
                Debug.Log("Stopping audio for: " + GetUsername());
            }
            #if UNITY_EDITOR
            else if(_isPlaying && _mumbleClient.HasPlayableAudio(Session))
            {

                //this._audioSource.clip.GetData(data, streamSamplePos);
                int numRead = _mumbleClient.LoadArrayWithVoiceData(Session, data, 0, data.Length);
                float[] tempData = new float[numRead];
                
                Array.Copy(data,tempData,tempData.Length);
                
                //float percentUnderrun = 1f - numRead / data.Length;

                //if (OnAudioSample != null)
                //    OnAudioSample(data, percentUnderrun);
               //while (frameData.Count > 0)
                //{
                    //var frame = frameData.Dequeue();
                    if (numRead > 0)
                    {
                        //_mumbleClient.LoadArrayWithVoiceData(Session, frame.Item1, 0, frame.Item1.Length);
                        this._audioSource.clip.SetData(tempData, this.streamSamplePos % bufferSamples);
                        //this.streamSamplePos += data.Length;
                        this.streamSamplePos += tempData.Length / this._audioSource.clip.channels;
                        //Debug.Log("FameDequqed: " + frame.Item1.Length);
                    }

                    //}
            }
            #endif
        }
        
        void OnAudioRead(float[] data)
        {
            Debug.Log("cubedata: " + data.Length);
            OnAudioFilterRead(data,2);
        }
    }
}
