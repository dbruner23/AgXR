using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget; // Added for Button
using Android.Media;  // Added for Audio
using Android.Content.PM; // Added for Permissions
using Android.Hardware.Camera2;
using Android.Graphics;
using Android.Speech; // Added for SpeechRecognizer
using Android.Content; // Added for Intent
using Android.Graphics; // Added for camera
using Android.Locations; // Added for GPS
using Android.Hardware.Camera2;

using Java.Util;
using Google.AR.Core;
using Google.AR.Core.Exceptions;
using Android.Opengl;
using Javax.Microedition.Khronos.Opengles;
using Javax.Microedition.Khronos.Egl;
using System.Runtime.InteropServices;
using GenerativeAI.Live;
using GenerativeAI.Types;
using GenerativeAI;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Collections.Generic;
using AgXR.App.Services;
using AgXR.App.Models;

namespace AgXR.App;

[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@style/AppTheme", ConfigurationChanges = Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.UiMode | Android.Content.PM.ConfigChanges.ScreenLayout | Android.Content.PM.ConfigChanges.SmallestScreenSize, Exported = true)]
public class MainActivity : Activity, View.IOnTouchListener, IRecognitionListener, GLSurfaceView.IRenderer, ILocationListener
{
    [DllImport("agxr_vision", EntryPoint = "Java_com_agxr_native_1lib_NativeLib_stringFromJNI")]
    private static extern IntPtr StringFromJNI(IntPtr env, IntPtr thiz);

    [DllImport("agxr_vision")]
    private static extern IntPtr GetVisionVersion(); 

    private Session? _arArgs;
    private Button? _btnPushToTalk;
    private Button? _btnTagLocation;
    private TextView? _txtStatus;
    private TextView? _txtResponse;
    private TextView? _txtGps;
    private TextView? _txtResponseCategory;
    private TextView? _txtResponseAction;
    private LinearLayout? _responsePanel;
    private GLSurfaceView? _surfaceView;
    private FrameLayout? _arOverlayContainer;
    
    // ARCore
    private ArRenderer? _arRenderer;
    private bool _viewportChanged = false;
    private int _viewportWidth;
    private int _viewportHeight;

    // Tap-to-place test
    private float _pendingTapX = -1;
    private float _pendingTapY = -1;
    private int _testMarkerCounter = -1000; // negative IDs won't clash with real geo-tags
    
    // Camera2 fallback (used when ARCore session creation fails, e.g. on emulators)
    private bool _useCamera2Fallback = false;
    private CameraDevice? _camera2Device;
    private CameraCaptureSession? _camera2Session;
    private SurfaceTexture? _camera2SurfaceTexture;
    private bool _camera2FrameAvailable = false;
    private readonly object _camera2Lock = new object();
    
    // Audio Configuration
    private const int SampleRate = 16000;
    private const ChannelIn ChannelConfigIn = ChannelIn.Mono;
    private const Encoding AudioFormat = Encoding.Pcm16bit;
    
    private AudioRecord? _audioRecord;
    private AudioTrack? _audioTrack;
    private bool _isRecording = false;
    private Thread? _recordingThread;
    private SpeechRecognizer? _speechRecognizer;
    private Intent? _speechRecognizerIntent;
    
    // Geo-tagging
    private GeoTagService? _geoTagService;
    private double _currentLatitude;
    private double _currentLongitude;
    private double _currentAccuracy;
    private bool _isTagging = false;
    private LocationManager? _locationManager;
    
    private const int RequestRecordAudioPermission = 200;
    private const int RequestLocationPermission = 201;
    private const int RequestCameraPermission = 202;
    
    private string? _capturedImagePath;

    // GL frame capture
    private volatile bool _capturePending = false;
    private TaskCompletionSource<string?>? _captureTcs;
    private readonly object _captureLock = new object();

    private Dictionary<int, Anchor> _activeAnchors = new Dictionary<int, Anchor>();
    private Dictionary<int, TextView> _activeMarkerViews = new Dictionary<int, TextView>();
    private bool _isDataLoaded = false;
    private List<GeoTag> _cachedTags = new List<GeoTag>();

    // Demo-mode fallback: when ARCore Geospatial doesn't reach Tracking (e.g. emulator),
    // we place session-local anchors in a ring around the user so the marker + edge-pointer
    // behaviour can still be demoed.
    private DateTime _sessionStartTimeUtc;
    private bool _earthEverTracked = false;
    private const double FallbackAfterSeconds = 8.0;


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
        
        // Wire up UI elements
        _btnPushToTalk = FindViewById<Button>(Resource.Id.btnPushToTalk);
        _btnTagLocation = FindViewById<Button>(Resource.Id.btnTagLocation);
        _txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
        _txtResponse = FindViewById<TextView>(Resource.Id.txtResponse);
        _txtGps = FindViewById<TextView>(Resource.Id.txtGps);
        _txtResponseCategory = FindViewById<TextView>(Resource.Id.txtResponseCategory);
        _txtResponseAction = FindViewById<TextView>(Resource.Id.txtResponseAction);
        _responsePanel = FindViewById<LinearLayout>(Resource.Id.responsePanel);
        _surfaceView = FindViewById<GLSurfaceView>(Resource.Id.surfaceview);
        _arOverlayContainer = FindViewById<FrameLayout>(Resource.Id.arOverlayContainer);
        
        if (_btnPushToTalk != null)
        {
            _btnPushToTalk.SetOnTouchListener(this);
        }

        _surfaceView?.SetOnTouchListener(this);

        if (_responsePanel != null)
        {
            _responsePanel.Click += (s, e) => DismissResponsePanel();
        }
        
        if (_btnTagLocation != null)
        {
            _btnTagLocation.Click += OnTagLocationClick;
        }

        var btnViewSaved = FindViewById<Button>(Resource.Id.btnViewSaved);
        if (btnViewSaved != null)
        {
            btnViewSaved.Click += (s, e) => {
                StartActivity(new Intent(this, typeof(SavedTagsActivity)));
            };
        }

        // Setup ARCore GLSurfaceView
        Android.Util.Log.Info("AgXR", "Setting up GLSurfaceView...");
        try
        {
            if (_surfaceView == null)
            {
                Android.Util.Log.Error("AgXR", "CRITICAL: _surfaceView is NULL!");
            }
            else
            {
                Android.Util.Log.Info("AgXR", "GLSurfaceView found, configuring...");
                _surfaceView.PreserveEGLContextOnPause = true;
                _surfaceView.SetEGLContextClientVersion(2);
                _surfaceView.SetEGLConfigChooser(8, 8, 8, 8, 16, 0); // Alpha used for display, depth 16
                _surfaceView.SetRenderer(this);
                Android.Util.Log.Info("AgXR", "Renderer set to MainActivity");
                _surfaceView.RenderMode = Rendermode.Continuously;
                _surfaceView.SetWillNotDraw(false);
                Android.Util.Log.Info("AgXR", "GLSurfaceView configured successfully");
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"GLSurfaceView setup failed: {ex.Message}");
            Android.Util.Log.Error("AgXR", $"Stack: {ex.StackTrace}");
        }

        _arRenderer = new ArRenderer();
        Android.Util.Log.Info("AgXR", "ArRenderer created");

        // Initialize Gemini Live
        SetupGemini();

        // Initialize GPS and Geo-tagging
        SetupGeoTagging();

        // Initialize Speech Recognizer for fallback mode
        InitializeSpeechRecognizer();

        // Check permissions which will then start things
        RequestMicPermission();
    }
    
    protected override void OnResume()
    {
        Android.Util.Log.Info("AgXR", "========== OnResume ENTRY ==========");
        try
        {
            base.OnResume();
            Android.Util.Log.Info("AgXR", "OnResume: base.OnResume() completed");

        // Initialize ARCore if camera permission is granted
        var cameraPermission = CheckSelfPermission(Android.Manifest.Permission.Camera);
        Android.Util.Log.Info("AgXR", $"Camera permission status: {cameraPermission}");

        if (cameraPermission == Android.Content.PM.Permission.Granted)
        {
            if (_arArgs == null)
            {
                Android.Util.Log.Info("AgXR", "Camera permission granted, initializing ARCore in OnResume");
                SetupARCore();
            }
        }

        // Request location updates when app is in foreground
        if (_locationManager != null && CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Android.Content.PM.Permission.Granted)
        {
            try
            {
                // Request updates every 1000ms (1 sec) or 1 meter change
                _locationManager.RequestLocationUpdates(LocationManager.GpsProvider, 1000, 1, this);
                _locationManager.RequestLocationUpdates(LocationManager.NetworkProvider, 1000, 1, this);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("AgXR", $"Resume location updates failed: {ex.Message}");
            }
        }

        try
        {
            Android.Util.Log.Info("AgXR", "OnResume: Resuming AR session and surface view...");
            _arArgs?.Resume();
            if (_surfaceView != null)
            {
                _surfaceView.OnResume();
                Android.Util.Log.Info("AgXR", "OnResume: GLSurfaceView.OnResume() called");
            }
            else
            {
                Android.Util.Log.Warn("AgXR", "OnResume: _surfaceView is null!");
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", "Error resuming AR Session: " + ex.Message);
            _arArgs = null;
        }

        _audioTrack?.Play();

        Android.Util.Log.Info("AgXR", "OnResume: Loading tags for AR...");
        LoadTagsForAR();
        Android.Util.Log.Info("AgXR", "========== OnResume EXIT ==========");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"EXCEPTION in OnResume: {ex.Message}");
            Android.Util.Log.Error("AgXR", $"Stack: {ex.StackTrace}");
        }
    }

    protected override void OnPause()
    {
        base.OnPause();
        // Stop location updates when app is backgrounded to save battery
        if (_locationManager != null)
        {
            _locationManager.RemoveUpdates(this);
        }
        
        if (_isRecording) StopRecording();
        _arArgs?.Pause();
        _surfaceView?.OnPause();
        _audioTrack?.Pause();
    }

    private void InitializeSpeechRecognizer()
    {
        if (SpeechRecognizer.IsRecognitionAvailable(this))
        {
            _speechRecognizer = SpeechRecognizer.CreateSpeechRecognizer(this);
            _speechRecognizer.SetRecognitionListener(this);
            
            _speechRecognizerIntent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            _speechRecognizerIntent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
            _speechRecognizerIntent.PutExtra(RecognizerIntent.ExtraLanguage, "en-US");
            _speechRecognizerIntent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
            _speechRecognizerIntent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
            
            Android.Util.Log.Info("AgXR", "SpeechRecognizer initialized");
        }
        else
        {
            Android.Util.Log.Warn("AgXR", "Speech recognition not available");
        }
    }

    // ... (Permission request method remains)

    // ... (SetupARCore remains)
    
    // SetupGemini
    private async void SetupGemini()
    {
        try 
        {
            string apiKey = Secrets.ApiKey; 
            Android.Util.Log.Info("AgXR", "Initializing Gemini with API key...");
            
            // Use our custom WebSocket client for Live API
            _customLiveClient = new GeminiLiveClient(apiKey);
            
            // Set up event handlers
            _customLiveClient.AudioReceived += (sender, audioData) => {
                Android.Util.Log.Info("AgXR", $"Audio received: {audioData.Length} bytes");
                InitializeAudioTrack();
                _audioTrack?.Write(audioData, 0, audioData.Length);
            };
            
            _customLiveClient.TextReceived += (sender, text) => {
                Android.Util.Log.Info("AgXR", $"Text received: {text}");
                RunOnUiThread(() => {
                    if (_txtResponse != null)
                    {
                        _txtResponse.Text = (_txtResponse.Text ?? "") + text;
                    }
                    if (_txtStatus != null)
                    {
                        _txtStatus.Text = "Response received";
                    }
                });
            };
            
            _customLiveClient.Connected += (sender, e) => {
                Android.Util.Log.Info("AgXR", "Custom WebSocket connected!");
            };
            
            _customLiveClient.Disconnected += (sender, e) => {
                Android.Util.Log.Warn("AgXR", "Custom WebSocket disconnected");
            };
            
            _customLiveClient.ErrorOccurred += (sender, error) => {
                Android.Util.Log.Error("AgXR", $"WebSocket error: {error}");
            };
            
            // Connect to WebSocket
            Android.Util.Log.Info("AgXR", "Connecting to Gemini Live API...");
            try 
            {
                await _customLiveClient.ConnectAsync("You are a helpful NZ farming assistant. Be concise.");
                
                // Wait a moment to see if connection stays open
                await Task.Delay(500);
                
                if (_customLiveClient.IsConnected)
                {
                    Android.Util.Log.Info("AgXR", "Gemini Live connected and stable!");
                    _liveApiConnected = true;
                }
                else
                {
                    throw new Exception("WebSocket disconnected after setup");
                }
            }
            catch (Exception wsEx)
            {
                Android.Util.Log.Warn("AgXR", $"Live API failed: {wsEx.Message}");
                _liveApiConnected = false;
                
                // Initialize text-based fallback with speech recognition
                var googleAi = new GoogleAi(apiKey);
                _textModel = googleAi.CreateGenerativeModel("gemini-2.0-flash");
                Android.Util.Log.Info("AgXR", "Text fallback initialized with speech recognition");
                
                // Update status on UI
                RunOnUiThread(() => {
                    if (_txtStatus != null)
                    {
                        _txtStatus.Text = "Voice Mode (Text Fallback)";
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Gemini Init Failed: {ex.Message}");
            Android.Util.Log.Error("AgXR", $"Stack: {ex.StackTrace}");
        }
    }
    
    private GeminiLiveClient? _customLiveClient;
    private bool _liveApiConnected = false;
    private GenerativeModel? _textModel;
    
    // Setup GPS and GeoTagging service
    private void SetupGeoTagging()
    {
        try
        {
            // Initialize geo-tag service
            _geoTagService = new GeoTagService(this, Secrets.ApiKey);
            
            // Request location permission
            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Android.Content.PM.Permission.Granted)
            {
                RequestPermissions(new[] { Android.Manifest.Permission.AccessFineLocation }, RequestLocationPermission);
            }
            else
            {
                UpdateLastLocation();
            }
            
            Android.Util.Log.Info("AgXR", "Geo-tagging service initialized");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"GeoTag setup failed: {ex.Message}");
        }
    }
    
    private void UpdateLastLocation()
    {
        try
        {
            _locationManager = (LocationManager?)GetSystemService(LocationService);
            if (_locationManager == null) return;
            
            // Try GPS first, then network
            var location = _locationManager.GetLastKnownLocation(LocationManager.GpsProvider) 
                ?? _locationManager.GetLastKnownLocation(LocationManager.NetworkProvider);
            
            if (location != null)
            {
                _currentLatitude = location.Latitude;
                _currentLongitude = location.Longitude;
                _currentAccuracy = location.Accuracy;
                
                RunOnUiThread(() => {
                    if (_txtGps != null)
                    {
                        _txtGps.Text = $"GPS: {location.Latitude:F5}, {location.Longitude:F5}";
                    }
                    // Update GPS icon color to green
                    var iconGps = FindViewById<ImageView>(Resource.Id.iconGps);
                    iconGps?.SetColorFilter(Android.Graphics.Color.Argb(255, 76, 175, 80)); // Green
                });
                
                Android.Util.Log.Info("AgXR", $"Location: {location.Latitude}, {location.Longitude}");
            }
            else
            {
                Android.Util.Log.Warn("AgXR", "No last known location available");
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Location failed: {ex.Message}");
        }
    }
    


        // ILocationListener Implementation
    public void OnLocationChanged(Location location)
    {
        try {
            if (location == null) return;
            
            // Capture values BEFORE RunOnUiThread to avoid location being disposed
            double lat = location.Latitude;
            double lon = location.Longitude;
            double acc = location.Accuracy;
            
            _currentLatitude = lat;
            _currentLongitude = lon;
            _currentAccuracy = acc;
            
            RunOnUiThread(() => {
                try {
                    // Check if activity is still valid
                    if (IsFinishing || IsDestroyed) return;
                    
                    if (_txtGps != null) 
                    {
                        _txtGps.Text = $"GPS: {lat:F5}, {lon:F5}";
                    }
                    
                    // Update GPS icon color to green (active) - with proper null check
                    var iconGps = FindViewById<ImageView>(Resource.Id.iconGps);
                    if (iconGps != null)
                    {
                        iconGps.SetColorFilter(Android.Graphics.Color.Argb(255, 76, 175, 80)); // Green
                    }
                } catch (Exception ex) {
                     Android.Util.Log.Error("AgXR", $"Error in UI update: {ex.Message}");
                }
            });
            
            Android.Util.Log.Debug("AgXR", $"Location update: {lat}, {lon}");
        } catch (Exception ex) {
            Android.Util.Log.Error("AgXR", $"Error in OnLocationChanged: {ex}");
        }
    }

    public void OnProviderDisabled(string provider) { }
    public void OnProviderEnabled(string provider) { }
    public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }
    
    // Tag Location button click handler
    private async void OnTagLocationClick(object? sender, EventArgs e)
    {
        if (_isTagging || _geoTagService == null) return;
        _isTagging = true;
        
        try
        {
            if (_txtStatus != null)
            {
                _txtStatus.Text = "Capturing...";
            }
            if (_btnTagLocation != null)
            {
                _btnTagLocation.Text = "📷";
            }

            // Capture the current frame from the live camera preview
            _capturedImagePath = await CaptureCurrentFrameAsync();

            if (string.IsNullOrEmpty(_capturedImagePath))
            {
                Android.Util.Log.Warn("AgXR", "Failed to capture frame");
            }
            else
            {
                Android.Util.Log.Info("AgXR", $"Frame captured: {_capturedImagePath}");
            }

            if (_txtStatus != null)
            {
                _txtStatus.Text = "Describe...";
            }
            if (_btnTagLocation != null)
            {
                _btnTagLocation.Text = "🎤 Describe";
            }
            
            // Start speech recognition to get description
            if (_speechRecognizer != null && _speechRecognizerIntent != null)
            {
                _speechRecognizer.StartListening(_speechRecognizerIntent);
                // The result will come through OnResults which will call ProcessGeoTag
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Tag location failed: {ex.Message}");
            if (_txtStatus != null)
            {
                _txtStatus.Text = "Error: " + ex.Message;
            }
            _isTagging = false;
        }
    }
    
    // Process the geo-tag after speech recognition
    private async Task ProcessGeoTagAsync(string voiceDescription)
    {
        try
        {
            if (_txtStatus != null)
            {
                _txtStatus.Text = "Processing...";
            }

            // Use the captured image path
            var imagePath = _capturedImagePath ?? "";

            // Call the geo-tag service
            var geoTag = await _geoTagService!.ProcessGeoTagAsync(
                imagePath,
                _currentLatitude,
                _currentLongitude,
                _currentAccuracy,
                voiceDescription
            );

            if (geoTag != null)
            {
                // Show result in response panel
                RunOnUiThread(() => {
                    ShowResponsePanel();
                    if (_txtResponseCategory != null)
                    {
                        _txtResponseCategory.Text = geoTag.Category.ToUpper();
                    }
                    if (_txtResponse != null)
                    {
                        _txtResponse.Text = geoTag.Description;
                    }

                    if (!string.IsNullOrEmpty(geoTag.Action))
                    {
                        if (_txtResponseAction != null)
                        {
                            _txtResponseAction.Text = $"Action: {geoTag.Action}";
                            _txtResponseAction.Visibility = ViewStates.Visible;
                        }
                    }
                    else
                    {
                        if (_txtResponseAction != null)
                        {
                            _txtResponseAction.Visibility = ViewStates.Gone;
                        }
                    }

                    if (_txtStatus != null)
                    {
                        _txtStatus.Text = "Tag saved!";
                    }
                });

                Android.Util.Log.Info("AgXR", $"GeoTag saved: {geoTag.Category} at {geoTag.Latitude}, {geoTag.Longitude}");
            }
            else
            {
                if (_txtStatus != null)
                {
                    _txtStatus.Text = "Failed to save tag";
                }
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"ProcessGeoTag failed: {ex.Message}");
            if (_txtStatus != null)
            {
                _txtStatus.Text = "Error: " + ex.Message;
            }
        }
        finally
        {
            _isTagging = false;
            if (_btnTagLocation != null)
            {
                _btnTagLocation.Text = "📍 Tag Location";
            }
        }
    }

    private void RequestMicPermission()
    {
        var permissions = new List<string>();
        
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Android.Content.PM.Permission.Granted)
        {
            permissions.Add(Android.Manifest.Permission.RecordAudio);
        }
        if (CheckSelfPermission(Android.Manifest.Permission.Camera) != Android.Content.PM.Permission.Granted)
        {
            permissions.Add(Android.Manifest.Permission.Camera);
        }
        
        if (permissions.Count > 0)
        {
            RequestPermissions(permissions.ToArray(), RequestRecordAudioPermission);
        }
        else
        {
            // Permissions already granted
        }
    }
   
    // ... (rest of methods)



    private long _responsePanelShownAtMs = 0;
    private const long ResponsePanelDismissGraceMs = 400;

    private void ShowResponsePanel()
    {
        if (_responsePanel != null) _responsePanel.Visibility = ViewStates.Visible;
        _responsePanelShownAtMs = Android.OS.SystemClock.UptimeMillis();
    }

    private void DismissResponsePanel()
    {
        if (_responsePanel == null || _responsePanel.Visibility != ViewStates.Visible) return;
        if (Android.OS.SystemClock.UptimeMillis() - _responsePanelShownAtMs < ResponsePanelDismissGraceMs) return;
        _responsePanel.Visibility = ViewStates.Gone;
        if (_txtResponse != null) _txtResponse.Text = "";
        if (_txtResponseAction != null) _txtResponseAction.Visibility = ViewStates.Gone;
        if (_txtStatus != null) _txtStatus.Text = "Ready";
    }

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

        // Tap on the AR surface: if a response is showing, dismiss it; otherwise place a test marker.
        if (v?.Id == Resource.Id.surfaceview && e?.Action == MotionEventActions.Up)
        {
            if (_responsePanel != null && _responsePanel.Visibility == ViewStates.Visible)
            {
                DismissResponsePanel();
                return true;
            }

            _pendingTapX = e.GetX();
            _pendingTapY = e.GetY();
            return true;
        }

        return false;
    }

    private void StartRecording()
    {
        if (_isRecording) return;
        _isRecording = true;
        if (_btnPushToTalk != null)
        {
            _btnPushToTalk.Text = "Listening...";
        }
        if (_txtStatus != null)
        {
            _txtStatus.Text = "Listening...";
        }
        if (_txtResponse != null)
        {
            _txtResponse.Text = ""; // Clear previous response
        }
        Android.Util.Log.Info("AgXR", $"StartRecording called, liveApiConnected={_liveApiConnected}");

        // Capture the current view in parallel so Gemini can answer visual questions.
        // Runs off the UI thread; result stored in _capturedImagePath by the time speech returns.
        _capturedImagePath = null;
        _ = CaptureCurrentFrameAsync().ContinueWith(t =>
        {
            _capturedImagePath = t.Result;
        }, TaskScheduler.Default);

        // If using fallback mode, use SpeechRecognizer
        if (!_liveApiConnected && _speechRecognizer != null && _speechRecognizerIntent != null)
        {
            Android.Util.Log.Info("AgXR", "Starting speech recognition...");
            _speechRecognizer.StartListening(_speechRecognizerIntent);
            return;
        }
        
        // Otherwise use audio streaming to Live API
        Android.Util.Log.Info("AgXR", "Starting audio streaming to Live API...");
        int bufferSize = AudioRecord.GetMinBufferSize(SampleRate, ChannelConfigIn, AudioFormat);
        _audioRecord = new AudioRecord(AudioSource.Mic, SampleRate, ChannelConfigIn, AudioFormat, bufferSize);
        
        if (_audioRecord.State == State.Initialized)
        {
            Android.Util.Log.Info("AgXR", "AudioRecord initialized, starting recording thread");
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
        if (_btnPushToTalk != null)
        {
            _btnPushToTalk.Text = GetString(Resource.String.push_to_talk);
        }
        
        // If using SpeechRecognizer, stop it
        if (!_liveApiConnected && _speechRecognizer != null)
        {
            _speechRecognizer.StopListening();
            return;
        }

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
    
    private async Task SendTextFallbackQuery(string userQuery, string? imagePath = null)
    {
        try
        {
            var hasImage = !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath);
            Android.Util.Log.Info("AgXR", $"Sending to Gemini: {userQuery} (image={hasImage})");

            var systemPrompt = hasImage
                ? "You are an expert NZ farming assistant. The attached image is what the user is currently seeing through their phone camera. Answer their question about what they're looking at, and give practical advice on nutrient management, pasture, livestock, or farm infrastructure as relevant. Be concise — 2-3 sentences unless detail is essential."
                : "You are an expert NZ farming assistant. Help with nutrient management and pasture analysis. Be concise.";

            var request = new GenerateContentRequest();
            request.AddText($"{systemPrompt}\n\nUser: {userQuery}");
            if (hasImage)
            {
                request.AddInlineFile(imagePath!);
            }

            var response = await _textModel!.GenerateContentAsync(request);
            var text = response.Text() ?? "Hello from Gemini!";
            Android.Util.Log.Info("AgXR", $"Gemini response: {text}");
            
            // Show response in the same panel used for tagging so tap-to-dismiss works consistently.
            RunOnUiThread(() => {
                ShowResponsePanel();
                if (_txtResponseCategory != null)
                {
                    _txtResponseCategory.Text = "ASSISTANT";
                }
                if (_txtResponseAction != null)
                {
                    _txtResponseAction.Visibility = ViewStates.Gone;
                }
                if (_txtResponse != null)
                {
                    _txtResponse.Text = text;
                }
                if (_txtStatus != null)
                {
                    _txtStatus.Text = "Response received";
                }
            });
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Text fallback error: {ex.Message}");
            RunOnUiThread(() => {
                if (_txtStatus != null)
                {
                    _txtStatus.Text = $"Error: {ex.Message}";
                }
            });
        }
    }
    
    // IRecognitionListener implementation
    public void OnResults(Bundle? results)
    {
        var matches = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        if (matches != null && matches.Count > 0)
        {
            var transcript = matches[0];
            Android.Util.Log.Info("AgXR", $"Speech recognized: {transcript}");
            
            RunOnUiThread(() => {
                if (_btnPushToTalk != null)
                {
                    _btnPushToTalk.Text = "🎤 Ask";
                }
            });
            
            // Route to appropriate handler based on mode
            if (_isTagging && !string.IsNullOrEmpty(transcript))
            {
                // Geo-tagging mode - process as geo-tag
                _ = ProcessGeoTagAsync(transcript);
            }
            else if (_textModel != null && !string.IsNullOrEmpty(transcript))
            {
                // Normal query mode — include captured frame so Gemini can answer visual questions
                _ = SendTextFallbackQuery(transcript, _capturedImagePath);
            }
        }
        _isRecording = false;
    }
    
    public void OnPartialResults(Bundle? partialResults)
    {
        var partial = partialResults?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        if (partial != null && partial.Count > 0)
        {
            Android.Util.Log.Debug("AgXR", $"Partial: {partial[0]}");
        }
    }
    
    public void OnError(SpeechRecognizerError error)
    {
        Android.Util.Log.Error("AgXR", $"Speech recognition error: {error}");
        RunOnUiThread(() => {
            if (_btnPushToTalk != null)
            {
                _btnPushToTalk.Text = GetString(Resource.String.push_to_talk);
            }
            Android.Widget.Toast.MakeText(this, $"Speech error: {error}", Android.Widget.ToastLength.Short)?.Show();
        });
        _isRecording = false;
    }
    
    public void OnBeginningOfSpeech() => Android.Util.Log.Debug("AgXR", "Speech started");
    public void OnEndOfSpeech() => Android.Util.Log.Debug("AgXR", "Speech ended");
    public void OnReadyForSpeech(Bundle? @params) => Android.Util.Log.Debug("AgXR", "Ready for speech");
    public void OnRmsChanged(float rmsdB) { } // Ignore RMS changes
    public void OnBufferReceived(byte[]? buffer) { } // Ignore buffer
    public void OnEvent(int eventType, Bundle? @params) { } // Ignore events



    private async void ProcessAudioInput()
    {
        int bufferSize = 1024;
        byte[] buffer = new byte[bufferSize];
        int chunkCount = 0;

        Android.Util.Log.Info("AgXR", $"ProcessAudioInput started, client connected: {_customLiveClient?.IsConnected}");

        while (_isRecording && _audioRecord != null)
        {
            int bytesRead = _audioRecord.Read(buffer, 0, bufferSize);
            if (bytesRead > 0)
            {
                if (_customLiveClient != null && _customLiveClient.IsConnected)
                {
                    byte[] dataToSend = new byte[bytesRead];
                    Array.Copy(buffer, dataToSend, bytesRead);
                    
                    try 
                    {
                        await _customLiveClient.SendAudioAsync(dataToSend);
                        chunkCount++;
                        if (chunkCount % 20 == 0) // Log every 20 chunks
                        {
                            Android.Util.Log.Info("AgXR", $"Sent {chunkCount} audio chunks");
                        }
                    }
                    catch (Exception ex)
                    {
                        Android.Util.Log.Error("AgXR", "Error sending audio: " + ex.Message);
                    }
                }
                else
                {
                    Android.Util.Log.Warn("AgXR", $"Client not connected, skipping audio (connected: {_customLiveClient?.IsConnected})");
                    break;
                }
            }
        }
        Android.Util.Log.Info("AgXR", $"ProcessAudioInput ended, total chunks: {chunkCount}");
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
            Android.Util.Log.Info("AgXR", "Initializing ARCore...");

            // Log ARCore availability (but don't block on it - sideloaded APK on emulator
            // won't pass RequestInstall since Play Store offers incompatible ARM version)
            try
            {
                var availability = ArCoreApk.Instance.CheckAvailability(this);
                Android.Util.Log.Info("AgXR", $"ARCore availability: {availability}");
            }
            catch (Exception checkEx)
            {
                Android.Util.Log.Warn("AgXR", $"ARCore availability check failed: {checkEx.Message}");
            }

            if (_arArgs == null)
            {
                Android.Util.Log.Info("AgXR", "Creating ARCore Session...");
                
                // Try standard session first - ARCore manages the camera automatically
                try
                {
                    _arArgs = new Session(this);
                    Android.Util.Log.Info("AgXR", "ARCore Session created (standard)");
                }
                catch (Exception stdEx)
                {
                    Android.Util.Log.Warn("AgXR", $"Standard session failed: {stdEx.Message}, trying SharedCamera...");
                    try
                    {
                        var features = new List<Session.Feature> { Session.Feature.SharedCamera };
                        _arArgs = new Session(this, features);
                        Android.Util.Log.Info("AgXR", "ARCore Session created with SharedCamera feature");
                    }
                    catch (Exception sharedEx)
                    {
                        Android.Util.Log.Error("AgXR", $"SharedCamera session also failed: {sharedEx.Message}");
                        Android.Util.Log.Info("AgXR", "Falling back to Camera2 API for camera preview");
                        _useCamera2Fallback = true;
                        RunOnUiThread(() => {
                            if (_txtStatus != null) _txtStatus.Text = "Using Camera2 fallback (ARCore unavailable)";
                        });
                        return;
                    }
                }

                var config = new Config(_arArgs);

                // Enable Geospatial API
                Android.Util.Log.Info("AgXR", "Configuring Geospatial mode...");
                try
                {
                    config.SetGeospatialMode(Config.GeospatialMode.Enabled);
                    Android.Util.Log.Info("AgXR", "Geospatial mode enabled");
                }
                catch (Exception geoEx)
                {
                    Android.Util.Log.Warn("AgXR", $"Geospatial mode not available: {geoEx.Message}");
                    // Continue without geospatial - basic AR still works
                }
                config.SetFocusMode(Config.FocusMode.Auto);

                _arArgs.Configure(config);
                Android.Util.Log.Info("AgXR", "ARCore Session configured");
            }

            _arArgs.Resume();
            _sessionStartTimeUtc = DateTime.UtcNow;
            _earthEverTracked = false;
            Android.Util.Log.Info("AgXR", "ARCore Session resumed successfully");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"ARCore Init Failed: {ex.Message}");
            Android.Util.Log.Error("AgXR", $"ARCore Stack: {ex.StackTrace}");
            _arArgs = null;
            _useCamera2Fallback = true;
            Android.Util.Log.Info("AgXR", "Falling back to Camera2 API");
            RunOnUiThread(() => {
                if (_txtStatus != null) _txtStatus.Text = "Using Camera2 fallback";
            });
        }
    }

    // GLSurfaceView.IRenderer Implementation
    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        Android.Util.Log.Info("AgXR", "OnSurfaceCreated called");
        GLES20.GlClearColor(0.1f, 0.1f, 0.1f, 1.0f);
        _arRenderer?.CreateOnGlThread();
        Android.Util.Log.Info("AgXR", "GL thread initialized");

        // Tell ARCore which GL texture to write camera frames into
        if (_arArgs != null && _arRenderer != null)
        {
            _arArgs.SetCameraTextureName(_arRenderer.TextureId);
            Android.Util.Log.Info("AgXR", $"Camera texture set: {_arRenderer.TextureId}");
        }

        // If Camera2 fallback is needed, set up camera now that we have the GL texture
        if (_useCamera2Fallback && _arRenderer != null)
        {
            SetupCamera2Fallback(_arRenderer.TextureId);
        }
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        Android.Util.Log.Info("AgXR", $"OnSurfaceChanged: {width}x{height}");
        _viewportWidth = width;
        _viewportHeight = height;
        _viewportChanged = true;
        GLES20.GlViewport(0, 0, width, height);
    }

    public void OnDrawFrame(IGL10? gl)
    {
        GLES20.GlClear(GLES20.GlColorBufferBit | GLES20.GlDepthBufferBit);

        // Camera2 fallback rendering path
        if (_useCamera2Fallback)
        {
            lock (_camera2Lock)
            {
                if (_camera2FrameAvailable && _camera2SurfaceTexture != null)
                {
                    _camera2SurfaceTexture.UpdateTexImage();
                    _camera2FrameAvailable = false;
                }
            }
            // Draw the camera texture using ArRenderer's shader (no ARCore Frame needed)
            if (_arRenderer != null)
            {
                DrawCamera2Fallback();
            }
            HandlePendingCapture();
            return;
        }

        if (_arArgs == null)
        {
            return;
        }

        try
        {
            if (_viewportChanged)
            {
                int displayRotation = (int)WindowManager!.DefaultDisplay!.Rotation;
                _arArgs.SetDisplayGeometry(displayRotation, _viewportWidth, _viewportHeight);
                _viewportChanged = false;
            }

            // Obtain the current frame from ARSession
            Frame frame = _arArgs.Update();

            // Process pending tap: hit-test and place a marker anchor
            if (_pendingTapX >= 0)
            {
                var hits = frame.HitTest(_pendingTapX, _pendingTapY);
                foreach (var hit in hits)
                {
                    if (hit.Trackable is Plane plane && plane.IsPoseInPolygon(hit.HitPose))
                    {
                        var anchor = hit.CreateAnchor();
                        int testId = _testMarkerCounter--;
                        _activeAnchors[testId] = anchor;
                        _cachedTags.Add(new AgXR.App.Models.GeoTag { Id = testId, Category = "TEST" });
                        Android.Util.Log.Info("AgXR", $"Test marker placed at {hit.HitPose.Tx():F2}, {hit.HitPose.Ty():F2}, {hit.HitPose.Tz():F2}");
                        break;
                    }
                }
                _pendingTapX = -1;
                _pendingTapY = -1;
            }

            // Draw camera background
            _arRenderer?.Draw(frame);

            // Handle Geo-spatial updates
            UpdateARMarkers(frame);

            HandlePendingCapture();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Render error: {ex.Message}");
        }
    }

    private void DrawCamera2Fallback()
    {
        if (_arRenderer == null) return;
        
        GLES20.GlUseProgram(_arRenderer.ProgramId);

        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _arRenderer.TextureId);
        GLES20.GlUniform1i(_arRenderer.TextureUniform, 0);

        GLES20.GlEnableVertexAttribArray(_arRenderer.PositionAttrib);
        GLES20.GlVertexAttribPointer(_arRenderer.PositionAttrib, 3, GLES20.GlFloat, false, 0, _arRenderer.VertexBuffer);

        GLES20.GlEnableVertexAttribArray(_arRenderer.TexCoordAttrib);
        GLES20.GlVertexAttribPointer(_arRenderer.TexCoordAttrib, 2, GLES20.GlFloat, false, 0, _arRenderer.TexCoordBuffer);

        GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);

        GLES20.GlDisableVertexAttribArray(_arRenderer.PositionAttrib);
        GLES20.GlDisableVertexAttribArray(_arRenderer.TexCoordAttrib);
    }

    private void SetupCamera2Fallback(int textureId)
    {
        Android.Util.Log.Info("AgXR", "Setting up Camera2 fallback...");
        try
        {
            var cameraManager = (CameraManager?)GetSystemService(CameraService);
            if (cameraManager == null)
            {
                Android.Util.Log.Error("AgXR", "Camera2: CameraManager is null");
                return;
            }

            string? cameraId = null;
            foreach (var id in cameraManager.GetCameraIdList())
            {
                var characteristics = cameraManager.GetCameraCharacteristics(id);
                var facing = (int)(characteristics.Get(CameraCharacteristics.LensFacing) ?? -1);
                if (facing == (int)LensFacing.Back)
                {
                    cameraId = id;
                    break;
                }
            }

            if (cameraId == null)
            {
                // Fall back to first camera
                var ids = cameraManager.GetCameraIdList();
                if (ids.Length > 0) cameraId = ids[0];
            }

            if (cameraId == null)
            {
                Android.Util.Log.Error("AgXR", "Camera2: No cameras found");
                return;
            }

            Android.Util.Log.Info("AgXR", $"Camera2: Using camera {cameraId}");

            // Create SurfaceTexture from the GL texture
            _camera2SurfaceTexture = new SurfaceTexture(textureId);
            _camera2SurfaceTexture.SetDefaultBufferSize(1920, 1080);
            _camera2SurfaceTexture.FrameAvailable += (sender, args) => {
                lock (_camera2Lock)
                {
                    _camera2FrameAvailable = true;
                }
            };

            var surface = new Surface(_camera2SurfaceTexture);

            cameraManager.OpenCamera(cameraId, new Camera2StateCallback(this, surface), null);
            Android.Util.Log.Info("AgXR", "Camera2: OpenCamera called");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Camera2 fallback failed: {ex.Message}");
            Android.Util.Log.Error("AgXR", $"Camera2 stack: {ex.StackTrace}");
        }
    }

    private class Camera2StateCallback : CameraDevice.StateCallback
    {
        private readonly MainActivity _activity;
        private readonly Surface _surface;

        public Camera2StateCallback(MainActivity activity, Surface surface)
        {
            _activity = activity;
            _surface = surface;
        }

        public override void OnOpened(CameraDevice camera)
        {
            Android.Util.Log.Info("AgXR", "Camera2: Camera opened!");
            _activity._camera2Device = camera;

            try
            {
                var captureRequestBuilder = camera.CreateCaptureRequest(CameraTemplate.Preview);
                captureRequestBuilder.AddTarget(_surface);
                
                var outputConfig = new Android.Hardware.Camera2.Params.OutputConfiguration(_surface);
                var sessionConfig = new Android.Hardware.Camera2.Params.SessionConfiguration(
                    0, // SessionConfiguration.SESSION_REGULAR
                    new[] { outputConfig },
                    Java.Util.Concurrent.Executors.NewSingleThreadExecutor()!,
                    new Camera2CaptureSessionCallback(_activity, captureRequestBuilder));

                camera.CreateCaptureSession(sessionConfig);
                Android.Util.Log.Info("AgXR", "Camera2: CreateCaptureSession called");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("AgXR", $"Camera2: Session creation failed: {ex.Message}");
            }
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            Android.Util.Log.Warn("AgXR", "Camera2: Disconnected");
            camera.Close();
            _activity._camera2Device = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            Android.Util.Log.Error("AgXR", $"Camera2: Error {error}");
            camera.Close();
            _activity._camera2Device = null;
        }
    }

    private class Camera2CaptureSessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly MainActivity _activity;
        private readonly CaptureRequest.Builder _requestBuilder;

        public Camera2CaptureSessionCallback(MainActivity activity, CaptureRequest.Builder requestBuilder)
        {
            _activity = activity;
            _requestBuilder = requestBuilder;
        }

        public override void OnConfigured(CameraCaptureSession session)
        {
            Android.Util.Log.Info("AgXR", "Camera2: Session configured, starting preview!");
            _activity._camera2Session = session;
            try
            {
                _requestBuilder.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.ContinuousPicture);
                session.SetRepeatingRequest(_requestBuilder.Build(), null, null);
                Android.Util.Log.Info("AgXR", "Camera2: Preview started!");
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("AgXR", $"Camera2: Preview start failed: {ex.Message}");
            }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            Android.Util.Log.Error("AgXR", "Camera2: Session configuration failed");
        }
    }

    private void UpdateARMarkers(Frame frame)
    {
        var camera = frame.Camera;
        if (camera.TrackingState != TrackingState.Tracking) return;

        var earth = _arArgs?.Earth;
        bool earthTracking = earth != null && earth.TrackingState == TrackingState.Tracking;
        if (earthTracking) _earthEverTracked = true;

        double elapsed = (DateTime.UtcNow - _sessionStartTimeUtc).TotalSeconds;
        bool useFallback = !earthTracking && !_earthEverTracked && elapsed > FallbackAfterSeconds;

        if (!earthTracking && !useFallback)
        {
            RunOnUiThread(() => {
                if (_txtStatus != null && _txtStatus.Text != "Waiting for Earth Tracking...")
                {
                    _txtStatus.Text = "Waiting for Earth Tracking...";
                }
            });
            return;
        }

        if (earthTracking)
        {
            var geospatialPose = earth!.CameraGeospatialPose;
            foreach (var tag in _cachedTags)
            {
                if (_activeAnchors.ContainsKey(tag.Id)) continue;
                double distance = CalculateDistance(geospatialPose.Latitude, geospatialPose.Longitude, tag.Latitude, tag.Longitude);
                if (distance < 200.0)
                {
                    CreateAnchorForTag(tag, earth);
                }
            }

            UpdateAnchorUI(frame);

            RunOnUiThread(() => {
                if (_txtStatus == null) return;
                var msg = geospatialPose.HorizontalAccuracy > 20.0
                    ? $"Low GPS Accuracy: {geospatialPose.HorizontalAccuracy:F1}m"
                    : "AR Ready";
                if (_txtStatus.Text != msg) _txtStatus.Text = msg;
            });
        }
        else
        {
            // Emulator / indoor fallback: place anchors in a ring around the user's current pose.
            foreach (var tag in _cachedTags)
            {
                if (_activeAnchors.ContainsKey(tag.Id)) continue;
                CreateLocalFallbackAnchor(tag, camera);
            }

            UpdateAnchorUI(frame);

            RunOnUiThread(() => {
                if (_txtStatus != null && _txtStatus.Text != "AR Ready (demo mode)")
                {
                    _txtStatus.Text = "AR Ready (demo mode)";
                }
            });
        }
    }

    // Session-local anchor placement used when ARCore Geospatial isn't available.
    // Each tag gets a deterministic position in a ring around the user's pose-at-tracking-time.
    private void CreateLocalFallbackAnchor(GeoTag tag, Google.AR.Core.Camera camera)
    {
        if (_arArgs == null) return;
        try
        {
            int slot = Math.Abs(tag.Id) % 8;
            double angle = slot * (2 * Math.PI / 8);
            const float radius = 5.0f;     // metres from the user
            float lateral = radius * (float)Math.Sin(angle);
            float depth = -radius * (float)Math.Cos(angle); // -Z is forward in camera space

            var localOffset = new Pose(
                new float[] { lateral, 0f, depth },
                new float[] { 0f, 0f, 0f, 1f });

            var worldPose = camera.Pose.Compose(localOffset);
            var anchor = _arArgs.CreateAnchor(worldPose);
            _activeAnchors[tag.Id] = anchor;
            Android.Util.Log.Info("AgXR", $"Fallback anchor for tag {tag.Id} at slot {slot} (lat={lateral:F1}, depth={depth:F1})");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Fallback anchor creation failed for tag {tag.Id}: {ex.Message}");
        }
    }
    
    // Capture current frame (Needs OpenGL implementation to read pixels)
    // UI-thread entry: request a frame capture. Completes when GL thread reads pixels after next draw.
    private Task<string?> CaptureCurrentFrameAsync()
    {
        lock (_captureLock)
        {
            if (_captureTcs != null && !_captureTcs.Task.IsCompleted)
                return _captureTcs.Task; // reuse in-flight request
            _captureTcs = new TaskCompletionSource<string?>();
            _capturePending = true;
        }
        _surfaceView?.RequestRender();
        return _captureTcs.Task;
    }

    // GL-thread: called at end of OnDrawFrame when a capture is pending.
    private void HandlePendingCapture()
    {
        if (!_capturePending) return;
        _capturePending = false;

        TaskCompletionSource<string?>? tcs;
        lock (_captureLock) { tcs = _captureTcs; }

        try
        {
            int w = _viewportWidth, h = _viewportHeight;
            if (w <= 0 || h <= 0) { tcs?.TrySetResult(null); return; }

            var buffer = Java.Nio.ByteBuffer.AllocateDirect(w * h * 4);
            buffer.Order(Java.Nio.ByteOrder.NativeOrder()!);
            GLES20.GlReadPixels(0, 0, w, h, GLES20.GlRgba, GLES20.GlUnsignedByte, buffer);
            buffer.Rewind();

            var raw = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!)!;
            raw.CopyPixelsFromBuffer(buffer);

            // GL origin is bottom-left, Android bitmap origin is top-left — flip Y.
            var matrix = new Android.Graphics.Matrix();
            matrix.PostScale(1f, -1f, w / 2f, h / 2f);
            var flipped = Bitmap.CreateBitmap(raw, 0, 0, w, h, matrix, true)!;
            raw.Recycle();

            // Downscale so Gemini upload is small; preserve aspect ratio.
            const int maxDim = 1024;
            Bitmap output = flipped;
            if (flipped.Width > maxDim || flipped.Height > maxDim)
            {
                float scale = (float)maxDim / System.Math.Max(flipped.Width, flipped.Height);
                int nw = (int)(flipped.Width * scale);
                int nh = (int)(flipped.Height * scale);
                output = Bitmap.CreateScaledBitmap(flipped, nw, nh, true)!;
                flipped.Recycle();
            }

            var dir = System.IO.Path.Combine(FilesDir!.AbsolutePath, "GeoTagImages");
            Directory.CreateDirectory(dir);
            var filePath = System.IO.Path.Combine(dir, $"capture_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.jpg");
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                output.Compress(Bitmap.CompressFormat.Jpeg!, 80, stream);
            }
            output.Recycle();

            Android.Util.Log.Info("AgXR", $"Captured frame to {filePath}");
            tcs?.TrySetResult(filePath);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Capture failed: {ex.Message}");
            tcs?.TrySetResult(null);
        }
    }

    private void CloseCamera()
    {
        // No-op for ARCore managed camera
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        CloseCamera();
        _arArgs?.Close();
        _audioTrack?.Release();
        _audioRecord?.Release();
    }

    private async void LoadTagsForAR()
    {
        // Always reload to get fresh "IsTracked" status
        // if (_isDataLoaded) return; 

        var db = new AgXR.App.Data.GeoTagDatabase();
        var allTags = await db.GetGeoTagsAsync();
        
        // Filter only tracked tags
        _cachedTags = allTags.Where(t => t.IsTracked).ToList();
        _isDataLoaded = true;

        Android.Util.Log.Info("AgXR", $"Loaded {_cachedTags.Count} tracked tags for AR");
        
        // Remove anchors for tags that are no longer tracked
        var currentIds = _activeAnchors.Keys.ToList();
        foreach (var id in currentIds)
        {
            if (!_cachedTags.Any(t => t.Id == id))
            {
                _activeAnchors.Remove(id);
                if (_activeMarkerViews.ContainsKey(id))
                {
                    RunOnUiThread(() => {
                        if (_activeMarkerViews.ContainsKey(id))
                        {
                            _activeMarkerViews[id].Visibility = ViewStates.Gone;
                            _arOverlayContainer?.RemoveView(_activeMarkerViews[id]);
                            _activeMarkerViews.Remove(id);
                        }
                    });
                }
            }
        }
    }

    private void CreateAnchorForTag(GeoTag tag, Earth earth)
    {
         try {
            // Place anchor 1.5 meters above the terrain
            // Rotation: Identity
            var anchor = earth.ResolveAnchorOnTerrain(
                tag.Latitude,
                tag.Longitude,
                1.5,
                0, 0, 0, 1);
            
            _activeAnchors[tag.Id] = anchor;
         }
         catch (Exception ex)
         {
            Android.Util.Log.Error("AgXR", $"Failed to create anchor for tag {tag.Id}: {ex.Message}");
            _activeAnchors.Remove(tag.Id);
         }
    }

    private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        var R = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private void UpdateAnchorUI(Frame frame)
    {
        var camera = frame.Camera;

        RunOnUiThread(() => {
           foreach (var kvp in _activeAnchors.ToList())
           {
                int tagId = kvp.Key;
                Anchor? anchor = kvp.Value;

                // Cleanup view if anchor is lost or null
                if (anchor == null)
                {
                     if (_activeMarkerViews.ContainsKey(tagId))
                        _activeMarkerViews[tagId].Visibility = ViewStates.Gone;
                     continue;
                }

                // Check Terrain State if applicable
                // Note: Not all anchors have TerrainState, but those created via ResolveAnchorOnTerrain do.
                // We safe check or just rely on TrackingState.
                if (anchor.TrackingState != TrackingState.Tracking)
                {
                    if (_activeMarkerViews.ContainsKey(tagId))
                        _activeMarkerViews[tagId].Visibility = ViewStates.Gone;
                    continue;
                }

                Pose? pose = anchor.Pose;
                if (pose == null) continue;

                // Get Screen Coordinates (returns [x, y, z_depth])
                float[]? screenCoord = GetScreenCoordinates(camera, pose);

                if (screenCoord == null) continue;


                // Check if visible on screen
                bool isVisible = (screenCoord[0] >= 0 && screenCoord[0] <= _viewportWidth &&
                                  screenCoord[1] >= 0 && screenCoord[1] <= _viewportHeight &&
                                  screenCoord[2] > 0); // z > 0 means in front of camera

                if (!_activeMarkerViews.ContainsKey(tagId))
                {
                    // Create simple marker view
                    var tv = new TextView(this);
                    tv.Text = _cachedTags.FirstOrDefault(t => t.Id == tagId)?.Category ?? "Marker";
                    tv.SetTextColor(Android.Graphics.Color.White);
                    tv.SetBackgroundColor(Android.Graphics.Color.Argb(150, 0, 0, 0));
                    tv.SetPadding(16, 8, 16, 8);
                    _arOverlayContainer?.AddView(tv, new FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WrapContent, 
                        ViewGroup.LayoutParams.WrapContent));
                    _activeMarkerViews[tagId] = tv;
                }

                var view = _activeMarkerViews[tagId];
                view.Visibility = ViewStates.Visible;

                if (isVisible)
                {
                    // Normal behavior: Position at screen coordinates
                    view.TranslationX = screenCoord[0] - (view.Width / 2f);
                    view.TranslationY = screenCoord[1] - (view.Height / 2f);
                    view.Rotation = 0; // Reset rotation
                    view.Text = _cachedTags.FirstOrDefault(t => t.Id == tagId)?.Category ?? "Marker";
                }
                else
                {
                    // Wayfinding: Point towards off-screen object
                    // Vector from center of screen
                    float centerX = _viewportWidth / 2f;
                    float centerY = _viewportHeight / 2f;
                    
                    float dirX, dirY;
                    
                    if (screenCoord[2] > 0) 
                    {
                        // In front, but off-screen
                        dirX = screenCoord[0] - centerX;
                        dirY = screenCoord[1] - centerY;
                    }
                    else
                    {
                        // Behind camera
                        // Invert directions to point "back"
                        // Or simply: push to edges. 
                        // If behind, x/y are projected inverted.
                        dirX = -(screenCoord[0] - centerX);
                        dirY = -(screenCoord[1] - centerY);
                        
                        // If directly behind, dirX/Y might be small, force direction?
                        // Usually screenCoord[0] grows large when w is small.
                    }

                    // Normalize direction
                    double length = Math.Sqrt(dirX * dirX + dirY * dirY);
                    if (length < 0.001) { dirX = 1; dirY = 0; length = 1; } // Avoid div0
                    
                    float normX = (float)(dirX / length);
                    float normY = (float)(dirY / length);
                    
                    // Clamp to screen edge with padding
                    float padding = 100f; // px
                    float edgeX = centerX + normX * (centerX - padding);
                    float edgeY = centerY + normY * (centerY - padding);
                    
                    // Constrain to box
                    // Simple logic: determine which edge is hit
                    float tanTheta = Math.Abs(dirY / dirX);
                    float tanScreen = (float)_viewportHeight / _viewportWidth;
                    
                    if (tanTheta < tanScreen)
                    {
                        // Hits Left/Right edge
                        edgeX = dirX > 0 ? _viewportWidth - padding : padding;
                        edgeY = centerY + normY * Math.Abs((edgeX - centerX) / normX);
                    }
                    else
                    {
                        // Hits Top/Bottom edge
                        edgeY = dirY > 0 ? _viewportHeight - padding : padding;
                        edgeX = centerX + normX * Math.Abs((edgeY - centerY) / normY);
                    }

                    view.TranslationX = edgeX - (view.Width / 2f);
                    view.TranslationY = edgeY - (view.Height / 2f);
                    
                    // Rotate arrow
                    double angle = Math.Atan2(normY, normX) * (180 / Math.PI);
                    view.Rotation = (float)angle;
                    view.Text = "➤"; // Arrow character
                }
           } 
        });
    }

    private float[]? GetScreenCoordinates(Google.AR.Core.Camera camera, Pose pose)
    {
        float[] viewMatrix = new float[16];
        camera.GetViewMatrix(viewMatrix, 0);
        
        float[] projectionMatrix = new float[16];
        camera.GetProjectionMatrix(projectionMatrix, 0, 0.1f, 100.0f);
        
        float[] viewProjection = new float[16];
        Android.Opengl.Matrix.MultiplyMM(viewProjection, 0, projectionMatrix, 0, viewMatrix, 0);
        
        float[] worldPos = new float[4] { pose.Tx(), pose.Ty(), pose.Tz(), 1.0f };
        float[] screenPos = new float[4];
        
        Android.Opengl.Matrix.MultiplyMV(screenPos, 0, viewProjection, 0, worldPos, 0);
        
        // Perspective division
        if (Math.Abs(screenPos[3]) < 0.001) return null;
        
        float ndcX = screenPos[0] / screenPos[3];
        float ndcY = screenPos[1] / screenPos[3];
        
        // Map NDC (-1 to 1) to Viewport (0 to Width/Height)
        float x = ((ndcX + 1) * _viewportWidth) / 2.0f;
        float y = _viewportHeight - (((ndcY + 1) * _viewportHeight) / 2.0f); // Flip Y for Android UI
        
        return new float[] { x, y, screenPos[3] }; // Return W (depth) as 3rd component
    }
}
