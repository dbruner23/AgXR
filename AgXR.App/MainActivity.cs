using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget; // Added for Button
using Android.Media;  // Added for Audio
using Android.Content.PM; // Added for Permissions
using Google.AR.Core;
using System.Runtime.InteropServices;
using GenerativeAI.Live;
using GenerativeAI.Types;
using GenerativeAI;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Collections.Generic;

namespace AgXR.App;

[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@style/AppTheme", ConfigurationChanges = Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.UiMode | Android.Content.PM.ConfigChanges.ScreenLayout | Android.Content.PM.ConfigChanges.SmallestScreenSize)]
public class MainActivity : Activity, View.IOnTouchListener
{
    [DllImport("agxr_vision", EntryPoint = "Java_com_agxr_native_1lib_NativeLib_stringFromJNI")]
    private static extern IntPtr StringFromJNI(IntPtr env, IntPtr thiz);

    [DllImport("agxr_vision")]
    private static extern IntPtr GetVisionVersion(); 

    private Session? _arArgs;
    private MultiModalLiveClient? _geminiClient;
    private Button? _btnPushToTalk;
    
    // Audio Configuration
    private const int SampleRate = 16000;
    private const ChannelIn ChannelConfigIn = ChannelIn.Mono;
    private const Encoding AudioFormat = Encoding.Pcm16bit;
    
    private AudioRecord? _audioRecord;
    private AudioTrack? _audioTrack;
    private bool _isRecording = false;
    private Thread? _recordingThread;
    
    private const int RequestRecordAudioPermission = 200;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Load the native library
        try {
             // Java.Lang.JavaSystem is the specialized access in .NET Android to avoid System namespace clash
             Java.Lang.JavaSystem.LoadLibrary("agxr_vision");
        } catch (Exception) { /* Handle or log */ }
        
        // Set our view from the "main" layout resource
        SetContentView(Resource.Layout.activity_main);
        
        _btnPushToTalk = FindViewById<Button>(Resource.Id.btnPushToTalk);
        if (_btnPushToTalk != null)
        {
            _btnPushToTalk.SetOnTouchListener(this);
        }

        // Initialize ARCore Geospatial
        SetupARCore();
        
        // Initialize Gemini Live
        SetupGemini();

        RequestMicPermission();
    }

    // ... (Permission request method remains)

    // ... (SetupARCore remains)
    
    // SetupGemini
    private async void SetupGemini()
    {
        try 
        {
            string apiKey = Secrets.ApiKey; 
            
            // Initialize GoogleAi to get the platform adapter manually since propery is protected
            // Args: apiKey, apiVersion, baseUrl, authenticator, isVertex, logger
            var adapter = new GoogleAIPlatformAdapter(apiKey, "v1beta", "https://generativelanguage.googleapis.com", null, false, null);
            
            // Configure generation settings
            var config = new GenerationConfig
            {
                ResponseModalities = new List<Modality> { Modality.AUDIO },
                SpeechConfig = new SpeechConfig { 
                    VoiceConfig = new VoiceConfig { PrebuiltVoiceConfig = new PrebuiltVoiceConfig { VoiceName = "Puck" } }
                }
            };

            // Initialize Live Client
            _geminiClient = new MultiModalLiveClient(adapter, "models/gemini-2.0-flash-exp", config);
            
            // Use lambda with type inference
            _geminiClient.AudioChunkReceived += (sender, e) => {
                 if (e.Buffer != null && e.Buffer.Length > 0)
                 {
                      InitializeAudioTrack(); 
                      _audioTrack?.Write(e.Buffer, 0, e.Buffer.Length);
                 }
            };
            
            // Connect
            await _geminiClient.ConnectAsync();
            
            // Setup session instructions
            var setup = new BidiGenerateContentSetup 
            {
                Model = "models/gemini-2.0-flash-exp",
                GenerationConfig = config,
                SystemInstruction = new Content { Parts = new List<Part> { new Part { Text = "You are an expert NZ farming assistant. You help with nutrient management and pasture analysis." } } }
            };
            
            await _geminiClient.SendSetupAsync(setup);
            
            Android.Util.Log.Info("AgXR", "Gemini Live Client Connected");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Gemini Init Failed: {ex.Message}");
        }
    }

    private void RequestMicPermission()
    {
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions(new string[] { Android.Manifest.Permission.RecordAudio }, RequestRecordAudioPermission);
        }
    }
   
    // ... (rest of methods)



    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (v?.Id == Resource.Id.btnPushToTalk && e != null)
        {
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    StartRecording();
                    return true;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    StopRecording();
                    return true;
            }
        }
        return false;
    }

    private void StartRecording()
    {
        if (_isRecording) return;
        _isRecording = true;
        _btnPushToTalk!.Text = "Listening...";
        
        int bufferSize = AudioRecord.GetMinBufferSize(SampleRate, ChannelConfigIn, AudioFormat);
        _audioRecord = new AudioRecord(AudioSource.Mic, SampleRate, ChannelConfigIn, AudioFormat, bufferSize);
        
        if (_audioRecord.State == State.Initialized)
        {
            _audioRecord.StartRecording();
            _recordingThread = new Thread(ProcessAudioInput);
            _recordingThread.Start();
        }
        else
        {
            Android.Util.Log.Error("AgXR", "AudioRecord initialization failed");
            _isRecording = false;
        }
    }

    private void StopRecording()
    {
        if (!_isRecording) return;
        _isRecording = false;
        _btnPushToTalk!.Text = GetString(Resource.String.push_to_talk);

        if (_audioRecord != null)
        {
            try 
            {
                _audioRecord.Stop();
                _audioRecord.Release();
            }
            catch (Exception ex) { Android.Util.Log.Error("AgXR", "Error stopping audio: " + ex.Message); }
            _audioRecord = null;
        }
    }



    private async void ProcessAudioInput()
    {
        int bufferSize = 1024;
        byte[] buffer = new byte[bufferSize];

        while (_isRecording && _audioRecord != null)
        {
            int bytesRead = _audioRecord.Read(buffer, 0, bufferSize);
            if (bytesRead > 0)
            {
                if (_geminiClient != null)
                {
                    byte[] dataToSend = new byte[bytesRead];
                    Array.Copy(buffer, dataToSend, bytesRead);
                    
                    try 
                    {
                        // SendAudioAsync takes byte[], mimeType, cancellationToken
                        await _geminiClient.SendAudioAsync(dataToSend);
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Error("AgXR", "Error sending audio: " + ex.Message);
                    }
                }
            }
        }
    }

    // Audio Playback
    private void InitializeAudioTrack()
    {
        if (_audioTrack == null)
        {
             var audioAttributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media) // Use Media instead of generic
                .SetContentType(AudioContentType.Speech)
                .Build();

             var audioFormat = new AudioFormat.Builder()
                .SetSampleRate(24000) // Gemini output is often 24kHz
                .SetEncoding(Encoding.Pcm16bit)
                .SetChannelMask(ChannelOut.Mono)
                .Build();

             int bufferSize = AudioTrack.GetMinBufferSize(24000, ChannelOut.Mono, Encoding.Pcm16bit);
             
             _audioTrack = new AudioTrack.Builder()
                .SetAudioAttributes(audioAttributes)
                .SetAudioFormat(audioFormat)
                .SetBufferSizeInBytes(bufferSize)
                .SetTransferMode(AudioTrackMode.Stream)
                .Build();
                
             _audioTrack.Play();
        }
    }

    private void SetupARCore()
    {
        try
        {
             // Vapolia ARCore: Session creation will throw if not available/installed suitable
            _arArgs = new Session(this);
            var config = new Config(_arArgs);
            
            // Enable Geospatial API
            config.SetGeospatialMode(Config.GeospatialMode.Enabled);
            
            _arArgs.Configure(config);
            _arArgs.Resume();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", "ARCore Init Failed: " + ex.Message);
        }
    }
    
    protected override void OnPause()
    {
        base.OnPause();
        if (_isRecording) StopRecording();
        _arArgs?.Pause();
        _audioTrack?.Pause();
    }
    
    protected override void OnResume()
    {
        base.OnResume();
        try { _arArgs?.Resume(); } catch {}
        _audioTrack?.Play();
    }
    
    protected override void OnDestroy()
    {
        base.OnDestroy();
        _arArgs?.Close();
        _audioTrack?.Release();
        _audioRecord?.Release();
    }
}
