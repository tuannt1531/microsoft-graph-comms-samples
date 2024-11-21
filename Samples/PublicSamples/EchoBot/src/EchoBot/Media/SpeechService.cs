using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using Microsoft.Skype.Bots.Media;
using Sprache;
using System.Runtime.InteropServices;
using Azure.Identity; 
using Azure;
using Azure.AI.OpenAI;


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
                    { "vi", new List<string> { "vi-VN", "vi-VN-HoaiMyNeural", "vietnamese" } },
                    { "en", new List<string> { "en-US", "en-US-JennyNeural", "english" } },
                    { "fr", new List<string> { "fr-FR", "fr-FR-DeniseNeural", "french" } },
                    { "zh", new List<string> { "zh-CN", "zh-CN-XiaoxiaoNeural", "chinese" } },
                    { "es", new List<string> { "es-ES", "es-ES-ElviraNeural", "spanish" } },
                    { "de", new List<string> { "de-DE", "de-DE-KatjaNeural", "german" } },
                    { "ja", new List<string> { "ja-JP", "ja-JP-AoiNeural", "japanese" } }
                };

                var stopRecognition1 = new TaskCompletionSource<int>();
                var stopRecognition2 = new TaskCompletionSource<int>();

                var v2EndpointUrl = new Uri($"wss://eastus.stt.speech.microsoft.com/speech/universal/v2");

                // Configure STT for each direction
                var speechConfig1 = SpeechConfig.FromEndpoint(v2EndpointUrl, _speechConfig.SubscriptionKey);
                var speechConfig2 = SpeechConfig.FromEndpoint(v2EndpointUrl, _speechConfig.SubscriptionKey);

                if (setting != null)
                {
                    speechConfig1.SpeechRecognitionLanguage = languageSettingMapping[setting.SourceLanguage][0];
                    speechConfig2.SpeechRecognitionLanguage = languageSettingMapping[setting.TargetLanguage][0];
                }
                else
                {
                    speechConfig1.SpeechRecognitionLanguage = "vi-VN";
                    speechConfig2.SpeechRecognitionLanguage = "en-US";
                }

                var autoDetectSourceLanguageConfig = AutoDetectSourceLanguageConfig.FromLanguages(new string[] { languageSettingMapping[setting.SourceLanguage][0], languageSettingMapping[setting.TargetLanguage][0] });

                // Separate audio input instances
                using var audioInput1 = AudioConfig.FromStreamInput(_audioInputStream);
                using var audioInput2 = AudioConfig.FromStreamInput(_audioInputStream2);

                // speechConfig1.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");
                // speechConfig2.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");

                var recognizer1 = new SpeechRecognizer(speechConfig1, autoDetectSourceLanguageConfig, audioInput1);
                var recognizer2 = new SpeechRecognizer(speechConfig2, autoDetectSourceLanguageConfig, audioInput2);

                ConfigureRecognizer(recognizer1, stopRecognition1, languageSettingMapping[setting.SourceLanguage][0], languageSettingMapping[setting.SourceLanguage][2], "Recognizer1");
                ConfigureRecognizer(recognizer2, stopRecognition2, languageSettingMapping[setting.TargetLanguage][0], languageSettingMapping[setting.TargetLanguage][2], "Recognizer2");

                // Start both recognizers in parallel and wait for them to complete
                var recognitionTasks = new[]
                {
                    RunRecognizerAsync(recognizer1, stopRecognition1, languageSettingMapping[setting.SourceLanguage][0], languageSettingMapping[setting.TargetLanguage][2]),
                    RunRecognizerAsync(recognizer2, stopRecognition2, languageSettingMapping[setting.TargetLanguage][0], languageSettingMapping[setting.SourceLanguage][2])
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

        private async Task RunRecognizerAsync(SpeechRecognizer recognizer, TaskCompletionSource<int> stopRecognition, string sourceLanguage, string targetLanguage)
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

        private async Task<string> TranslateTextUsingAzureOpenAI(string inputText, string targetLanguage)
        {
            // Set up Azure OpenAI client
            AzureOpenAIClient azureClient = new AzureOpenAIClient(new Uri("https://tamn-m3nxsxt9-eastus2.cognitiveservices.azure.com/"), new AzureKeyCredential("FQ4txV5FuBvyT8GO2YcaK1eVambFWVSXxr0kioe1DI31NWVvjE5oJQQJ99AKACHYHv6XJ3w3AAAAACOGvuRi"));
            ChatClient chatClient = azureClient.GetChatClient("gpt-4");

            var chatMessages = new ChatMessage[]
            {
                new SystemChatMessage($"You are a translator assistant that help to translates input text into {targetLanguage}. If have no input text, please return empty string."),
                new UserChatMessage($"Please translate this text: {inputText} into {targetLanguage}. Only output translated text.")
            };

            // Complete the chat conversation GetChatCompletionsAsync
            ChatCompletion completion = await chatClient.CompleteChatAsync(chatMessages);

            var translatedText = completion.Content[0].Text.Trim();
            _logger.LogInformation($"OpenAIClient TRANSLATED TEXT: {translatedText}");
            return translatedText;
        }

        // Configure event handlers for recognizer
        private void ConfigureRecognizer(SpeechRecognizer recognizer, TaskCompletionSource<int> stopRecognition, string sourceLanguage, string targetLanguage, string recognizerLabel)
        {
            recognizer.Recognizing += (s, e) =>
            {
                _logger.LogInformation($">>> {recognizerLabel} RECOGNIZING: Text={e.Result.Text}");
            };

            recognizer.Recognized += async (s, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    var recognizedText = e.Result.Text;
                    _logger.LogInformation($">>> {recognizerLabel} RECOGNIZED TEXT: {recognizedText}");

                    // Detect the source language
                    var detectedLanguage = e.Result.Properties.GetProperty(PropertyId.SpeechServiceConnection_AutoDetectSourceLanguageResult);
                    _logger.LogInformation($">>> {recognizerLabel} DETECTED LANGUAGE: {detectedLanguage} sourceLanguage {sourceLanguage}");
                    if (recognizedText != "" && detectedLanguage == sourceLanguage) {
                        // Step 2: Use Azure OpenAI LLM to translate the recognized text
                        var translatedText = await TranslateTextUsingAzureOpenAI(recognizedText, targetLanguage);
                        _logger.LogInformation($">>> {recognizerLabel} TRANSLATED TEXT: {translatedText}");
                        await TextToSpeech(translatedText);
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
