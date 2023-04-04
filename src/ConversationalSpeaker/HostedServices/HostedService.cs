﻿using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI;
using Microsoft.SemanticKernel.AI.ChatCompletion;
using Microsoft.SemanticKernel.Configuration;
using Microsoft.SemanticKernel.Orchestration;
using NetCoreAudio;

namespace ConversationalSpeaker
{
    /// <summary>
    /// A hosted service providing the primary conversation loop for Semantic Kernel with OpenAI ChatGPT.
    /// </summary>
    internal class HostedService : IHostedService, IDisposable
    {
        private readonly ILogger<HostedService> _logger;

        // Semantic Kernel chat support
        private readonly IKernel _semanticKernel;
        private readonly IDictionary<string, ISKFunction> _speechSkill;
        private readonly AzCognitiveServicesWakeWordListener _wakeWordListener;
        private readonly IChatCompletion _chatCompletion;
        private readonly OpenAIChatHistory _chatHistory;
        private readonly ChatRequestSettings _chatRequestSettings;

        private Task _executeTask;
        private readonly CancellationTokenSource _cancelToken = new();

        // Notification sound support
        private readonly string _notificationSoundFilePath;
        private readonly Player _player;

        /// <summary>
        /// Constructor
        /// </summary>
        public HostedService(
            AzCognitiveServicesWakeWordListener wakeWordListener,
            IKernel semanticKernel,
            AzCognitiveServicesSpeechSkill speechSkill,
            IOptions<OpenAiServiceOptions> openAIOptions,
            IOptions<GeneralOptions> generalOptions,
            ILogger<HostedService> logger)
        {
            _logger = logger;

            _chatRequestSettings = new ChatRequestSettings()
            {
                MaxTokens = openAIOptions.Value.MaxTokens,
                Temperature = openAIOptions.Value.Temperature,
                FrequencyPenalty = openAIOptions.Value.FrequencyPenalty,
                PresencePenalty = openAIOptions.Value.PresencePenalty,
                TopP = openAIOptions.Value.TopP
            };

            _wakeWordListener = wakeWordListener;
            _semanticKernel = semanticKernel;

            _semanticKernel.Config.AddOpenAIChatCompletion("chat", openAIOptions.Value.Model, openAIOptions.Value.Key, openAIOptions.Value.OrganizationId);
            _chatCompletion = _semanticKernel.GetService<IChatCompletion>();
            _chatHistory = (OpenAIChatHistory)_chatCompletion.CreateNewChat(generalOptions.Value.SystemPrompt);

            _speechSkill = _semanticKernel.ImportSkill(speechSkill);

            _notificationSoundFilePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Handlers", "bing.mp3");
            _player = new Player();
        }

        /// <summary>
        /// Start the service.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _executeTask = ExecuteAsync(_cancelToken.Token);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Primary service logic loop.
        /// </summary>
        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Play a notification to let the user know we have started listening for the wake phrase.
                await _player.Play(_notificationSoundFilePath);

                // Wait for wake word or phrase
                if (!await _wakeWordListener.WaitForWakeWordAsync(cancellationToken))
                {
                    continue;
                }

                await _player.Play(_notificationSoundFilePath);

                // Say hello on startup
                await _semanticKernel.RunAsync("Hallo, hier spricht Samantha!", _speechSkill["Speak"]);

                // Start listening
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Listen to the user
                    SKContext context = await _semanticKernel.RunAsync(_speechSkill["Listen"]);
                    string userSpoke = context.Result;

                    // Get a reply from the AI and add it to the chat history.
                    string reply = string.Empty;
                    try
                    {
                        _chatHistory.AddUserMessage(userSpoke);
                        reply = await _chatCompletion.GenerateMessageAsync(_chatHistory, _chatRequestSettings);

                        // Add the interaction to the chat history.
                        _chatHistory.AddAssistantMessage(reply);
                    }
                    catch (AIException aiex)
                    {
                        _logger.LogError($"OpenAI returned an error. {aiex.ErrorCode}: {aiex.Message}");
                        reply = "OpenAI returned an error. Please try again.";
                    }

                    // Speak the AI's reply
                    await _semanticKernel.RunAsync(reply, _speechSkill["Speak"]);

                    // If the user said "ciao" - stop listening and wait for the wake work again.
                    if (userSpoke.StartsWith("ciao", StringComparison.InvariantCultureIgnoreCase))
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Stop a running service.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cancelToken.Cancel();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public virtual void Dispose()
        {
            _cancelToken.Dispose();
            _wakeWordListener.Dispose();
        }
    }
}