using System;
using UniRx;
using UniRx.Triggers;
using UnityEngine;

namespace Scripts
{
    [RequireComponent(typeof(AudioSource))]
    public class OnAudioFilterTest : MonoBehaviour
    {
        private AudioClip clp;
        
        
        private void Start()
        {
            GetComponent<AudioSource>().playOnAwake = true;
            GetComponent<AudioSource>().loop = true;
            //GetComponent<AudioSource>().clip.PCMReaderCallback =  
           // AudioClip clip = AudioClip.Create("clip", 20 * 48000 / 1000 + 1920 + 48000, 2, 48000, true, OnAudioRead);
            //GetComponent<AudioSource>().clip = clip;
            GetComponent<AudioSource>().Play();
             AudioSettings.OnAudioConfigurationChanged += changed => { Debug.Log("deviceChanged = " + changed);};
            this.gameObject.AddComponent<ObservableOnAudioFilerReadTriger>()
                .OnAudioFilterReadAsObservable()
                .Subscribe(x => Debug.Log("cube: " + x.Item1.Length), () => Debug.Log("destroy"));
           
        }
        
        void OnAudioRead(float[] data)
        {
            Debug.Log("cubedata: " + data.Length);
        }

        private void Update()
        {
            if (!GetComponent<AudioSource>().isPlaying)
            {
                GetComponent<AudioSource>().Play(1);
                Debug.Log("delayed audio started");
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            Debug.Log("AudioFilterRead: " + data.Length);
        }
        
        
        
    }
}