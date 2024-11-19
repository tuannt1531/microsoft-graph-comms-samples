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
        private readonly PushAudioInputStream _audioInputStream2 = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
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
                // await ProcessSpeech();
                await ProcessBiDirectionalSpeech();
            }

            try
            {
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);
                    _audioInputStream.Write(buffer);
                    _audioInputStream2.Write(buffer);
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
                _audioInputStream2.Close();

                _audioInputStream.Dispose();
                _audioInputStream2.Dispose();
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

        private async Task ProcessBiDirectionalSpeech()
        {
            try
            {
                var setting = _redisService.GetSettings(_callId);
                var languageSettingMapping = new Dictionary<string, List<string>>
                {
                    { "vi", new List<string> { "vi-VN", "vi-VN-HoaiMyNeural" } },
                    { "en", new List<string> { "en-US", "en-US-JennyNeural" } },
                    { "fr", new List<string> { "fr-FR", "fr-FR-DeniseNeural" } },
                    { "zh", new List<string> { "zh-CN", "zh-CN-XiaoxiaoNeural" } },
                    { "es", new List<string> { "es-ES", "es-ES-ElviraNeural" } },
                    { "de", new List<string> { "de-DE", "de-DE-KatjaNeural" } },
                    { "ja", new List<string> { "ja-JP", "ja-JP-AoiNeural" } }
                };

                var stopRecognition1 = new TaskCompletionSource<int>();
                var stopRecognition2 = new TaskCompletionSource<int>();

                var v2EndpointUrl = new Uri($"wss://eastus.stt.speech.microsoft.com/speech/universal/v2");

                var translationConfig1 = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, _speechConfig.SubscriptionKey);
                var translationConfig2 = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, _speechConfig.SubscriptionKey);

                if (setting != null)
                {
                    translationConfig1.SpeechRecognitionLanguage = languageSettingMapping[setting.SourceLanguage][0];
                    translationConfig1.AddTargetLanguage(setting.TargetLanguage);
                    translationConfig1.VoiceName = languageSettingMapping[setting.TargetLanguage][1];

                    translationConfig2.SpeechRecognitionLanguage = languageSettingMapping[setting.TargetLanguage][0];
                    translationConfig2.AddTargetLanguage(setting.SourceLanguage);
                    translationConfig2.VoiceName = languageSettingMapping[setting.SourceLanguage][1];
                }
                else
                {
                    translationConfig1.SpeechRecognitionLanguage = "vi-VN";
                    translationConfig1.AddTargetLanguage("en");
                    translationConfig1.VoiceName = "en-US-JennyNeural";

                    translationConfig2.SpeechRecognitionLanguage = "en-US";
                    translationConfig2.AddTargetLanguage("vi");
                    translationConfig2.VoiceName = "vi-VN-HoaiMyNeural";
                }

                translationConfig1.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");
                translationConfig2.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");

                var autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromLanguages(new string[] { "vi-VN", "en-US" });
                if (setting != null)
                {
                    autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromLanguages(new string[] { languageSettingMapping[setting.SourceLanguage][0], languageSettingMapping[setting.TargetLanguage][0] });
                }

                // Separate audio input instances
                using var audioInput1 = AudioConfig.FromStreamInput(_audioInputStream);
                using var audioInput2 = AudioConfig.FromStreamInput(_audioInputStream2);

                _translationRecognizer = new TranslationRecognizer(translationConfig1, autoDetectSourceLanguageConfig, audioInput1);
                _translationRecognizer2 = new TranslationRecognizer(translationConfig2, autoDetectSourceLanguageConfig, audioInput2);

                ConfigureRecognizer(_translationRecognizer, stopRecognition1, languageSettingMapping[setting.SourceLanguage][0], "Recognizer1");
                ConfigureRecognizer(_translationRecognizer2, stopRecognition2, languageSettingMapping[setting.TargetLanguage][0], "Recognizer2");

                // Start both recognizers in parallel and wait for them to complete
                var recognitionTasks = new[]
                {
                    RunRecognizerAsync(_translationRecognizer, stopRecognition1),
                    RunRecognizerAsync(_translationRecognizer2, stopRecognition2)
                };

                await Task.WhenAll(recognitionTasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Caught Exception during ProcessBiDirectionalSpeech.");
            }
            finally
            {
                _isDraining = false;
            }
        }

        private async Task RunRecognizerAsync(TranslationRecognizer recognizer, TaskCompletionSource<int> stopRecognition)
        {
            try
            {
                await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
                await stopRecognition.Task;  // Wait for the recognition session to complete
                await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in RunRecognizerAsync.");
            }
        }

        private void ConfigureRecognizer(TranslationRecognizer recognizer, TaskCompletionSource<int> stopRecognition, string sourceLanguage, string recognizerLabel)
        {
            recognizer.Recognizing += (s, e) =>
            {
                _logger.LogInformation($"{recognizerLabel} RECOGNIZING: {e.Result.Text}");
                foreach (var element in e.Result.Translations)
                {
                    _logger.LogInformation($"{recognizerLabel} TRANSLATING into '{element.Key}': {element.Value}");
                }
            };

            recognizer.Recognized += async (s, e) =>
            {
                if (e.Result.Reason == ResultReason.TranslatedSpeech)
                {
                    _logger.LogInformation($"{recognizerLabel} RECOGNIZED: {e.Result.Text}");

                    var detectedLanguage = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);
                    _logger.LogInformation($"{recognizerLabel} detectedLanguage: {detectedLanguage} - sourceLanguage {sourceLanguage}");
                    if (detectedLanguage == sourceLanguage)
                    {
                        foreach (var element in e.Result.Translations)
                        {
                            _logger.LogInformation($"{recognizerLabel} TRANSLATING into '{element.Key}': {element.Value}");
                            await TextToSpeech(element.Value);
                        }
                    }
                }
            };

            recognizer.Canceled += (s, e) =>
            {
                _logger.LogInformation($"{recognizerLabel} CANCELED: Reason={e.Reason}");
                if (e.Reason == CancellationReason.Error)
                {
                    _logger.LogInformation($"ErrorDetails={e.ErrorDetails}");
                }
                stopRecognition.TrySetResult(0);
            };

            recognizer.SessionStarted += (s, e) => _logger.LogInformation($"{recognizerLabel} Session started.");
            recognizer.SessionStopped += (s, e) =>
            {
                _logger.LogInformation($"{recognizerLabel} Session stopped.");
                stopRecognition.TrySetResult(0);
            };
        }

        private async Task ProcessSpeech()
        {
            try
            {
                var setting = _redisService.GetSettings(_callId);
                Dictionary<string, List<string>> languageSettingMapping = new Dictionary<string, List<string>>();
                languageSettingMapping["vi"] = new List<string> { "vi-VN", "vi-VN-HoaiMyNeural" };
                languageSettingMapping["en"] = new List<string> { "en-US", "en-US-JennyNeural" };
                languageSettingMapping["fr"] = new List<string> { "fr-FR", "fr-FR-DeniseNeural" };
                languageSettingMapping["zh"] = new List<string> { "zh-CN", "zh-CN-XiaoxiaoNeural" };
                languageSettingMapping["es"] = new List<string> { "es-ES", "es-ES-ElviraNeural" };
                languageSettingMapping["de"] = new List<string> { "de-DE", "de-DE-KatjaNeural" };
                languageSettingMapping["ja"] = new List<string> { "ja-JP", "ja-JP-AoiNeural" };

                var stopRecognition = new TaskCompletionSource<int>();
                var stopRecognition2 = new TaskCompletionSource<int>();

                var v2EndpointInString = String.Format("wss://{0}.stt.speech.microsoft.com/speech/universal/v2", "eastus");
                var v2EndpointUrl = new Uri(v2EndpointInString);

                var translationConfig = SpeechTranslationConfig.FromEndpoint(v2EndpointUrl, _speechConfig.SubscriptionKey);

                //var translationConfig = SpeechTranslationConfig.FromSubscription(_speechConfig.SubscriptionKey, _speechConfig.Region);
                //string serviceRegion = "eastus";
                //string endpointString = "wss://eastus.stt.speech.microsoft.com/speech/universal/v2";

                if (setting != null)
                {
                    translationConfig.SpeechRecognitionLanguage = languageSettingMapping[setting.SourceLanguage][0];
                    translationConfig.AddTargetLanguage(setting.TargetLanguage);
                    translationConfig.VoiceName = languageSettingMapping[setting.TargetLanguage][1];
                }
                else
                {
                    translationConfig.SpeechRecognitionLanguage = "vi-VN";
                    translationConfig.AddTargetLanguage("en");
                    translationConfig.VoiceName = "en-US-JennyNeural";
                }

                //translationConfig.EndpointId = endpointString;
                translationConfig.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");

                // Tạo cấu hình tự động phát hiện ngôn ngữ
                var autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromLanguages(new string[] { "vi-VN", "en-US" });
                if (setting != null)
                {
                    autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromLanguages(new string[] { languageSettingMapping[setting.SourceLanguage][0], languageSettingMapping[setting.TargetLanguage][0] });
                }

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
                            var sourceLanguage = "vi-VN";
                            if (setting != null)
                            {
                                _logger.LogInformation($">>>setting: {setting.SourceLanguage} {setting.TargetLanguage}");
                                sourceLanguage = languageSettingMapping[setting.SourceLanguage][0];
                            }
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
