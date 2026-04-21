using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgXR.App;

/// <summary>
/// Custom WebSocket client for Gemini Live API.
/// Implements direct connection to wss://generativelanguage.googleapis.com/ws/...
/// </summary>
public class GeminiLiveClient : IDisposable
{
    private readonly string _apiKey;
    private readonly string _model;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isConnected;
    
    public event EventHandler<byte[]>? AudioReceived;
    public event EventHandler<string>? TextReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    
    private const string WS_ENDPOINT = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    
    public bool IsConnected => _isConnected;
    
    public GeminiLiveClient(string apiKey, string model = "models/gemini-2.0-flash-live-001")
    {
        _apiKey = apiKey;
        _model = model;
    }
    
    public async Task ConnectAsync(string? systemInstruction = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _webSocket = new ClientWebSocket();
            
            // Add API key as query parameter
            var uri = new Uri($"{WS_ENDPOINT}?key={_apiKey}");
            
            Android.Util.Log.Info("AgXR", $"Connecting to: {WS_ENDPOINT}...");
            await _webSocket.ConnectAsync(uri, _cts.Token);
            
            if (_webSocket.State == WebSocketState.Open)
            {
                Android.Util.Log.Info("AgXR", "WebSocket connected, sending setup...");
                _isConnected = true;
                Connected?.Invoke(this, EventArgs.Empty);
                
                // Send setup message
                await SendSetupAsync(systemInstruction);
                
                // Start receiving
                _ = ReceiveLoopAsync(_cts.Token);
            }
            else
            {
                throw new Exception($"WebSocket state is {_webSocket.State}");
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Connection error: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex.Message);
            _isConnected = false;
            throw;
        }
    }
    
    private async Task SendSetupAsync(string? systemInstruction)
    {
        // Create setup message according to Gemini Live API spec
        var setup = new
        {
            setup = new
            {
                model = _model,
                generationConfig = new
                {
                    responseModalities = new[] { "TEXT" }
                },
                systemInstruction = systemInstruction != null ? new
                {
                    parts = new[] { new { text = systemInstruction } }
                } : null
            }
        };
        
        var json = JsonSerializer.Serialize(setup, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        Android.Util.Log.Info("AgXR", $"Sending setup: {json}");
        
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts!.Token);
        Android.Util.Log.Info("AgXR", "Setup sent successfully");
    }
    
    public async Task SendAudioAsync(byte[] audioData)
    {
        if (!_isConnected || _webSocket?.State != WebSocketState.Open)
        {
            ErrorOccurred?.Invoke(this, "WebSocket not connected");
            return;
        }
        
        try
        {
            // Send audio as realtime input
            var message = new
            {
                realtimeInput = new
                {
                    mediaChunks = new[]
                    {
                        new
                        {
                            mimeType = "audio/pcm;rate=16000",
                            data = Convert.ToBase64String(audioData)
                        }
                    }
                }
            };
            
            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts!.Token);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
        }
    }
    
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();
        
        try
        {
            while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Android.Util.Log.Info("AgXR", "WebSocket closed by server");
                    _isConnected = false;
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    break;
                }
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    
                    if (result.EndOfMessage)
                    {
                        var message = messageBuilder.ToString();
                        messageBuilder.Clear();
                        ProcessMessage(message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Receive error: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex.Message);
        }
        finally
        {
            _isConnected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }
    
    private void ProcessMessage(string message)
    {
        try
        {
            Android.Util.Log.Debug("AgXR", $"Received: {message.Substring(0, Math.Min(200, message.Length))}...");
            
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            
            // Check for server content with audio
            if (root.TryGetProperty("serverContent", out var serverContent))
            {
                if (serverContent.TryGetProperty("modelTurn", out var modelTurn))
                {
                    if (modelTurn.TryGetProperty("parts", out var parts))
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            // Check for inline audio data
                            if (part.TryGetProperty("inlineData", out var inlineData))
                            {
                                if (inlineData.TryGetProperty("data", out var data))
                                {
                                    var audioBytes = Convert.FromBase64String(data.GetString()!);
                                    Android.Util.Log.Info("AgXR", $"Audio received: {audioBytes.Length} bytes");
                                    AudioReceived?.Invoke(this, audioBytes);
                                }
                            }
                            
                            // Check for text
                            if (part.TryGetProperty("text", out var text))
                            {
                                TextReceived?.Invoke(this, text.GetString()!);
                            }
                        }
                    }
                }
            }
            
            // Check for setup complete
            if (root.TryGetProperty("setupComplete", out _))
            {
                Android.Util.Log.Info("AgXR", "Setup complete received!");
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("AgXR", $"Message parse error: {ex.Message}");
        }
    }
    
    public async Task DisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
        }
        catch { }
        finally
        {
            _isConnected = false;
            _webSocket?.Dispose();
            _webSocket = null;
        }
    }
    
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();
    }
}
