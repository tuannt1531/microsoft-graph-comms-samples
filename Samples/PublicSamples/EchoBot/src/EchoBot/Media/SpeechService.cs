using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Skype.Bots.Media;
using Sprache;
using System.Runtime.InteropServices;

namespace EchoBot.Media
{
    public class SpeechService
    {
        private bool _isRunning = false;
        protected bool _isDraining;
        private readonly ILogger _logger;
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();
        private readonly SpeechConfig _speechConfig;
        private readonly SpeechSynthesizer _synthesizer;
        private TranslationRecognizer _translationRecognizer;
        private TranslationRecognizer _translationRecognizer2;

        // public event EventHandler<MediaStreamEventArgs> OnSendMediaBufferEventArgs;

        private string _callId;
        private RedisService _redisService;

        public SpeechService(AppSettings settings, ILogger logger, string callId)
        {
            _logger = logger;

            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;
            //_speechConfig.SpeechSynthesisLanguage = "vi";
            //_speechConfig.SpeechRecognitionLanguage = "vi";

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);

            _callId = callId;
            _redisService = new RedisService(settings.RedisConnection);
        }

        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            if (!_isRunning)
            {
                Start();
                await ProcessSpeech();
            }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);
                    _audioInputStream.Write(buffer);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception happened writing to input stream");
            }
        }

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            if (SendMediaBuffer != null)
            {
                SendMediaBuffer(this, e);
            }
        }

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task ShutDownAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_isRunning)
            {
                await _translationRecognizer.StopContinuousRecognitionAsync();
                _translationRecognizer.Dispose();
                await _translationRecognizer2.StopContinuousRecognitionAsync();
                _translationRecognizer2.Dispose();
                _audioInputStream.Close();

                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();
                _synthesizer.Dispose();

                _isRunning = false;
            }
        }

        private void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
            }
        }

        public async Task StartTranslationAsync(string sourceLanguage, string targetLanguage) 
        {
          try
            {
                Dictionary<string, List<string>> languageSettingMapping = new Dictionary<string, List<string>>();
                languageSettingMapping["vi"] = new List<string> { "vi-VN", "vi-VN-HoaiMyNeural" };
                languageSettingMapping["en"] = new List<string> { "en-US", "en-US-JennyNeural" };
                languageSettingMapping["fr"] = new List<string> { "fr-FR", "fr-FR-DeniseNeural" };
                languageSettingMapping["zh"] = new List<string> { "zh-CN", "zh-CN-XiaoxiaoNeural" };
                languageSettingMapping["es"] = new List<string> { "es-ES", "es-ES-ElviraNeural" };
                languageSettingMapping["de"] = new List<string> { "de-DE", "de-DE-KatjaNeural" };
                languageSettingMapping["ja"] = new List<string> { "ja-JP", "ja-JP-AoiNeural" };

                var stopRecognition = new TaskCompletionSource<int>();

                var v2EndpointInString = String.Format("wss://{0}.stt.speech.microsoft.com/speech/universal/v2", "eastus");
                var v2EndpointUrl = new Uri(v2EndpointInString);

                var translationConfig = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, _speechConfig.SubscriptionKey);

                //var translationConfig = SpeechTranslationConfig.FromSubscription(_speechConfig.SubscriptionKey, _speechConfig.Region);
                //string serviceRegion = "eastus";
                //string endpointString = "wss://eastus.stt.speech.microsoft.com/speech/universal/v2";

                translationConfig.SpeechRecognitionLanguage = languageSettingMapping[sourceLanguage][0];
                translationConfig.AddTargetLanguage(targetLanguage);
                translationConfig.VoiceName = languageSettingMapping[targetLanguage][1];
                
                //translationConfig.EndpointId = endpointString;
                translationConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");

                // Tạo cấu hình tự động phát hiện ngôn ngữ
                autoDetectSzourceLanguageConfig = AutoDetectSourceLanguageConfig.FromLanguages(new string[] { languageSettingMapping[sourceLanguage][0], languageSettingMapping[targetLanguage][0] });

                using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                {
                    // Sử dụng cấu hình phát hiện ngôn ngữ
                    _translationRecognizer = new TranslationRecognizer(translationConfig, autoDetectSourceLanguageConfig, audioInput);
                    //_translationRecognizer = new TranslationRecognizer(translationConfig, audioInput);
                }

                _translationRecognizer.Recognizing += (s, e) =>
                {
                    _logger.LogInformation($"RECOGNIZING: {e.Result.Text}");
                    foreach (var element in e.Result.Translations)
                    {
                        _logger.LogInformation($"TRANSLATING into '{element.Key}': {element.Value}");
                    }
                };

                _translationRecognizer.Recognized += async (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.TranslatedSpeech)
                    {
                        _logger.LogInformation($"RECOGNIZED: {e.Result.Text}");

                        // Lấy thông tin ngôn ngữ phát hiện được
                        var detectedLanguage = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);
                        // _logger.LogInformation($"Detected Language: {detectedLanguage}");

                        foreach (var element in e.Result.Translations)
                        {
                            _logger.LogInformation($"TRANSLATING into '{element.Key}': {element.Value}");
                            //await TextToSpeech(element.Value + " and ");

                            //// Chỉ thực hiện Text-to-Speech nếu ngôn ngữ phát hiện được là tiếng Việt
                            sourceLanguage = languageSettingMapping[sourceLanguage][0];
                            _logger.LogInformation($">>>detectedLanguage: {detectedLanguage} - sourceLanguage {sourceLanguage}");
                            if (detectedLanguage == sourceLanguage)
                            {
                                await TextToSpeech(element.Value);
                            }
                            else
                            {
                                await TextToSpeech(" ");
                            }
                        }
                    }
                };

                _translationRecognizer.Canceled += (s, e) =>
                {
                    _logger.LogInformation($"CANCELED: Reason={e.Reason}");
                    if (e.Reason == CancellationReason.Error)
                    {
                        _logger.LogInformation($"ErrorDetails={e.ErrorDetails}");
                    }
                    stopRecognition.TrySetResult(0);
                };

                _translationRecognizer.SessionStarted += (s, e) =>
                {
                    _logger.LogInformation("Session started.");
                };

                _translationRecognizer.SessionStopped += (s, e) =>
                {
                    _logger.LogInformation("Session stopped.");
                    stopRecognition.TrySetResult(0);
                };

                await _translationRecognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
                Task.WaitAny(new[] { stopRecognition.Task });
                await _translationRecognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Caught Exception");
            }
        }

        private async Task ProcessSpeech()
        {
            var setting = _redisService.GetSettings(_callId);
            if (setting != null) {
                    await StartTranslationAsync(setting.SourceLanguage, setting.TargetLanguage)
                    await StartTranslationAsync(setting.TargetLanguage, setting.SourceLanguage)
                }else {
                    await StartTranslationAsync("vi", "en")
                    await StartTranslationAsync("en", "vi")
                }
            _isDraining = false;
        }

        private async Task TextToSpeech(string text)
        {
            SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text);
            using (var stream = AudioDataStream.FromResult(result))
            {
                var currentTick = DateTime.Now.Ticks;
                MediaStreamEventArgs args = new MediaStreamEventArgs
                {
                    AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                };
                OnSendMediaBufferEventArgs(this, args);
            }
        }
    }
}
