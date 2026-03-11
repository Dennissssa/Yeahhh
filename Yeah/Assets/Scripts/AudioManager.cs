using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;

public class AudioManager : MonoBehaviour
{
    //Audio player components
    public AudioSource EffectsSource;
    public AudioSource MusicSource;

    public List<AudioClip> EffectsList = new List<AudioClip>();
    public List<AudioClip> MusicList = new List<AudioClip>();

    public float soundBuffer = 0.01f;

    //Random pitch adjustments
    public float LowPitchRand = 0.9f;
    public float HighPitchRand = 1.1f;

    private static AudioManager _instance;

    public static AudioManager Instance
    {
        get
        {
            if (_instance == null)
            {
                Debug.LogError("Audio manager is NULL. FUCK!");
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
        }
        else if (_instance != this)
        {
            Destroy(gameObject);
        }

        DontDestroyOnLoad(gameObject);
    }

    public void PlaySound(int Index)
    {
        EffectsSource.clip = EffectsList[Index];
        //if (EffectsSource.isPlaying &&
        //    EffectsSource.clip.name == EffectsList[Index].name &&
        //    EffectsSource.time < soundBuffer)
        //{
        //    return;
        //}
        //else
        {
            EffectsSource.Play();
        }
    }

    public void PlayMusic(int Index)
    {
        if (MusicSource.isPlaying)
        {
            MusicSource.Stop();
        }

        MusicSource.clip = MusicList[Index];
        MusicSource.Play();
    }

    //public void PlayRandom(AudioClip clip)
    //{
    //    float randomPitch = Random.Range(LowPitchRand, HighPitchRand);

    //    EffectsSource.pitch = randomPitch;
    //    EffectsSource.clip = clip;
    //    EffectsSource.Play();
    //}
}
