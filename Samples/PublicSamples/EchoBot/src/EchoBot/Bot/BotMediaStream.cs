// ***********************************************************************
// Assembly         : EchoBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : bcage29
// Last Modified On : 10-17-2023
// ***********************************************************************
// <copyright file="BotMediaStream.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// </copyright>
// <summary>The bot media stream.</summary>
// ***********************************************************************-
using EchoBot.Media;
using EchoBot.Util;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using Microsoft.Skype.Internal.Media.Services.Common;
using System.Runtime.InteropServices;

namespace EchoBot.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        private AppSettings _settings;

        /// <summary>
        /// The participants
        /// </summary>
        internal List<IParticipant> participants;

        /// <summary>
        /// The audio socket
        /// </summary>
        private readonly IAudioSocket _audioSocket;
        /// <summary>
        /// The media stream
        /// </summary>
        private readonly ILogger _logger;
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private readonly TaskCompletionSource<bool> audioSendStatusActive;
        private readonly TaskCompletionSource<bool> startVideoPlayerCompleted;
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private List<AudioMediaBuffer> audioMediaBuffers = new List<AudioMediaBuffer>();
        private int shutdown;
        private readonly MediaFrameSourceComponent mediaFrameSourceComponent;
        private int shutdown;
        private MediaSendStatus videoMediaSendStatus = MediaSendStatus.Inactive;
        private MediaSendStatus vbssMediaSendStatus = MediaSendStatus.Inactive;
        private MediaSendStatus audioSendStatus = MediaSendStatus.Inactive;
        private readonly ILocalMediaSession mediaSession;

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream" /> class.
        /// </summary>
        /// <param name="mediaSession">The media session.</param>
        /// <param name="callId">The call identity</param>
        /// <param name="graphLogger">The Graph logger.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="settings">Azure settings</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        public BotMediaStream(
            ILocalMediaSession mediaSession,
            CallHandler callHandler,
            Pipeline pipeline, 
            ITeamsBot teamsBot, 
            Exporter exporter,
            string callId,
            IGraphLogger graphLogger,
            ILogger logger,
            AppSettings settings
        )
            : base(graphLogger)
        {
            ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));
            ArgumentVerifier.ThrowOnNullArgument(settings, nameof(settings));

            _settings = settings;
            _logger = logger;

            this.mediaSession = mediaSession;

            this.participants = new List<IParticipant>();

            this.mediaFrameSourceComponent = new MediaFrameSourceComponent(pipeline, callHandler, _logger);

            this.audioSendStatusActive = new TaskCompletionSource<bool>();
            this.startVideoPlayerCompleted = new TaskCompletionSource<bool>();

            // Subscribe to the audio media.
            this._audioSocket = mediaSession.AudioSocket;
            if (this._audioSocket == null)
            {
                throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            }

            var ignoreTask = this.StartAudioVideoFramePlayerAsync().ForgetAndLogExceptionAsync(this.GraphLogger, "Failed to start the player");

            this._audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;            

            this._audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;

            this._audioSocket.DominantSpeakerChanged += OnDominantSpeakerChanged;

            if (_settings.UseSpeechService)
            {
                _languageService = new SpeechService(_settings, _logger, callId);
                _languageService.SendMediaBuffer += this.OnSendMediaBuffer;
            }

            _redisService = new RedisService(_settings.RedisConnection);
            _callId = callId;

            this.mediaFrameSourceComponent.Audio.Parallel(
                    (id, stream) =>
                    {
                        // Extract and persist audio streams with the original timestamps for each buffer
                        stream.Process<(AudioBuffer, DateTime), AudioBuffer>((tuple, _, emitter) =>
                        {
                            (var audioBuffer, var originatingTime) = tuple;
                            if (originatingTime > emitter.LastEnvelope.OriginatingTime)
                            {
                                // Out-of-order messages are ignored
                                emitter.Post(audioBuffer, originatingTime);
                            }
                        }).Write($"Participants.{id}.Audio", exporter);
                    },
                    branchTerminationPolicy: BranchTerminationPolicy<string, (AudioBuffer, DateTime)>.Never(),
                    name: "PersistParticipantAudio");
            teamsBot.AudioOut?.Write("Bot.Audio", exporter);
            this.mediaFrameSourceComponent.Audio.PipeTo(teamsBot.AudioIn);
            teamsBot.AudioOut?.Do(buffer =>
            {
                if (this.audioSendStatus == MediaSendStatus.Active && teamsBot.EnableAudioOutput)
                {
                    IntPtr unmanagedPointer = Marshal.AllocHGlobal(buffer.Length);
                    Marshal.Copy(buffer.Data, 0, unmanagedPointer, buffer.Length);
                    this.SendAudio(new AudioSendBuffer(unmanagedPointer, buffer.Length, AudioFormat.Pcm16K));
                    Marshal.FreeHGlobal(unmanagedPointer);
                }
            });
        }

        /// <summary>
        /// Gets the participants.
        /// </summary>
        /// <returns>List&lt;IParticipant&gt;.</returns>
        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        /// <inheritdoc/>   
        protected override void Dispose(bool disposing)
        {
            // Event Dispose of the bot media stream object
            base.Dispose(disposing);

            if (Interlocked.CompareExchange(ref this.shutdown, 1, 1) == 1)
            {
                return;
            }

            if (this.audioSocket != null)
            {
                this.audioSocket.AudioSendStatusChanged -= this.OnAudioSendStatusChanged;
                this.audioSocket.AudioMediaReceived -= this.OnAudioMediaReceived;
            }
        }        

        /// <summary>
        /// Shut down.
        /// </summary>
        /// <returns><see cref="Task" />.</returns>
        public async Task ShutdownAsync()
        {
            if (Interlocked.CompareExchange(ref this.shutdown, 1, 1) == 1)
            {
                return;
            }

            await this.startVideoPlayerCompleted.Task.ConfigureAwait(false);

            // unsubscribe
            if (this._audioSocket != null)
            {
                this._audioSocket.AudioSendStatusChanged -= this.OnAudioSendStatusChanged;
            }

            // shutting down the players
            if (this.audioVideoFramePlayer != null)
            {
                await this.audioVideoFramePlayer.ShutdownAsync().ConfigureAwait(false);
            }

            // make sure all the audio and video buffers are disposed, it can happen that,
            // the buffers were not enqueued but the call was disposed if the caller hangs up quickly
            foreach (var audioMediaBuffer in this.audioMediaBuffers)
            {
                audioMediaBuffer.Dispose();
            }

            _logger.LogInformation($"disposed {this.audioMediaBuffers.Count} audioMediaBUffers.");

            this.audioMediaBuffers.Clear();
        }

        /// <summary>
        /// Initialize AV frame player.
        /// </summary>
        /// <returns>Task denoting creation of the player with initial frames enqueued.</returns>
        private async Task StartAudioVideoFramePlayerAsync()
        {
            try
            {
                _logger.LogInformation("Send status active for audio and video Creating the audio video player");
                this.audioVideoFramePlayerSettings =
                    new AudioVideoFramePlayerSettings(new AudioSettings(20), new VideoSettings(), 1000);
                this.audioVideoFramePlayer = new AudioVideoFramePlayer(
                    (AudioSocket)_audioSocket,
                    null,
                    this.audioVideoFramePlayerSettings);

                _logger.LogInformation("created the audio video player");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create the audioVideoFramePlayer with exception");
            }
            finally
            {
                this.startVideoPlayerCompleted.TrySetResult(true);
            }
        }

        /// <summary>
        /// Callback for informational updates from the media plaform about audio status changes.
        /// Once the status becomes active, audio can be loopbacked.
        /// </summary>
        /// <param name="sender">The audio socket.</param>
        /// <param name="e">Event arguments.</param>
        private void OnAudioSendStatusChanged(object? sender, AudioSendStatusChangedEventArgs e)
        {
            _logger.Info($"[AudioSendStatusChangedEventArgs(MediaSendStatus={e.MediaSendStatus})]");
            this.audioSendStatus = e.MediaSendStatus;
        }

        /// <summary>
        /// Receive audio from subscribed participant.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The audio media received arguments.</param>
        private async void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
        {
            try
            {
                this.mediaFrameSourceComponent.Received(e.Buffer);
                e.Buffer.Dispose();
            }
            catch (Exception ex)
            {
                this.logger.Error(ex);
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        /// <summary>
        /// Sends an <see cref="AudioMediaBuffer"/> to the call from the Bot's audio feed.
        /// </summary>
        /// <param name="buffer">The audio buffer to send.</param>
        private void SendAudio(AudioMediaBuffer buffer)
        {
            // Send the audio to our outgoing video stream
            try
            {
                this.audioSocket.Send(buffer);
            }
            catch (Exception ex)
            {
                this.logger.Error(ex, $"[OnAudioReceived] Exception while calling audioSocket.Send()");
            }
        }
    }
}

