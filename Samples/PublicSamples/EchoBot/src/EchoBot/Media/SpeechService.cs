using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Skype.Bots.Media;
using Sprache;
using System.Runtime.InteropServices;
using Azure.Identity; 
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;


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
        private string _openAIKey;
        private LoggingService _loggingService;

        public SpeechService(AppSettings settings, ILogger logger, string callId)
        {
            _logger = logger;

            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);

            _callId = callId;
            _redisService = new RedisService(settings.RedisConnection);
            _openAIKey = settings.OpenAIKey;
            _loggingService = new LoggingService($"{callId}.txt");
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

        private async Task<string> TranslateTextUsingAzureOpenAI(string inputText, string targetLanguage)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew(); // Start measuring time

            ChatClient client = new(model: "gpt-4o", apiKey: _openAIKey);

            ChatCompletion completion = client.CompleteChat($"Please translate this text into {targetLanguage}: '{inputText}'. Please output only translated text. IF have no result, please output empty.");
            var translatedText =  completion.Content[0].Text.Trim();

            stopwatch.Stop(); // Stop measuring time
            _logger.LogInformation($">>> OPENAI Translation completed. Translated text: '{translatedText}'. Time taken: {stopwatch.ElapsedMilliseconds} ms.");

            return translatedText;
        }


        private async Task ProcessSpeech()
        {
            try
            {
                var setting = _redisService.GetSettings(_callId);
                var languageSettingMapping = new Dictionary<string, List<string>>
                {
                    { "vi", new List<string> { "vi-VN", "vi-VN-HoaiMyNeural", "vietnamese" } },
                    { "en", new List<string> { "en-US", "en-US-JennyNeural", "english" } },
                    { "fr", new List<string> { "fr-FR", "fr-FR-DeniseNeural", "french" } },
                    { "zh", new List<string> { "zh-CN", "zh-CN-XiaoxiaoNeural", "chinese" } },
                    { "es", new List<string> { "es-ES", "es-ES-ElviraNeural", "spanish" } },
                    { "de", new List<string> { "de-DE", "de-DE-KatjaNeural", "german" } },
                    { "ja", new List<string> { "ja-JP", "ja-JP-AoiNeural", "japanese" } }
                };

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
                    _logger.LogInformation($">>> recordSetting: {recordSetting.Record}");
                    if (e.Result.Reason == ResultReason.TranslatedSpeech)
                    {
                        if (e.Result.Text != "") {
                            _logger.LogInformation($">>> RECOGNIZED: {e.Result.Text}");
                            await _loggingService.Log($"RECOGNIZED: {e.Result.Text}");

                            // Lấy thông tin ngôn ngữ phát hiện được
                            var detectedLanguage = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);
                            _logger.LogInformation($">>> Detected Language: {detectedLanguage}");

                            var targetLanguage = languageSettingMapping[setting.TargetLanguage][2];
                            if (detectedLanguage == languageSettingMapping[setting.TargetLanguage][0]) {
                                targetLanguage = languageSettingMapping[setting.SourceLanguage][2];
                            }

                            _logger.LogInformation($">>> OPENAI TRANSLATING into {targetLanguage}");
                        
                            var translatedText = await TranslateTextUsingAzureOpenAI(e.Result.Text, targetLanguage);
                            _logger.LogInformation($">>> OPENAI translatedText {translatedText}");
                            await _loggingService.Log($"OPENAI TRANSLATED: {translatedText}");
                            foreach (var element in e.Result.Translations)
                            {
                                await _loggingService.Log($"AZURE TRANSLATED: {element.Value}");
                            }

                            if (translatedText != "")
                            {
                                await TextToSpeech(translatedText);
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
