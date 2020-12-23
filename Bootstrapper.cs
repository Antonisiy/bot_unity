using NTS.Logging;
using NTS.Telegram.ApiClient;
using NTS.Telegram.DataAccessLayer;
using NTS.Telegram.Implementation;
using NTS.Telegram.Implementation.Processors;
using NTS.Telegram.Implementation.Services;
using NTS.Telegram.Implementation.StateMachine;
using NTS.Telegram.Implementation.StateMachine.Dialog;
using NTS.Telegram.Implementation.StateMachine.States;
using NTS.Telegram.Management.Monitoring.Tracing;
using NTS.Telegram.Push;
using NTS.Telegram.Push.InlineReceivers.SqlServer;
using NTS.Telegram.Push.InlineReceivers.WebHook;
using NTS.Telegram.Runtime.Configuration;
using NTS.Telegram.Runtime.ServiceModel;
using NTS.Telegram.ServiceModel.Push;
using NTS.Telegram.Services;
using NTS.Telegram.Services.AddressList;
using NTS.Telegram.Services.EmployeesSource;
using NTS.Telegram.Services.ServiceDesk;
using NTS.Telegram.Types;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NTS.Telegram.Implementation.StateMachine.States.Employee.Portal;
using NTS.Telegram.Push.InlineReceivers.JiraHttpRequest;
using Unity;
using Unity.Injection;
using Unity.Lifetime;
using Unity.Registration;
using Unity.RegistrationByConvention;
using Unity.Resolution;

// ReSharper disable ArgumentsStyleOther

namespace NTS.Telegram.Runtime.Host
{
    internal class Bootstrapper : IBootstrapper
    {

        #region Constructor
        public Bootstrapper(IUnityContainer container)
        {
            _bootstrappingContainer = CreateBoostrapperContainer(container);
        }
        #endregion

        #region Fields
        private readonly IUnityContainer _bootstrappingContainer;
        #endregion

        #region Methods
        private IUnityContainer CreateBoostrapperContainer(IUnityContainer applicationContainer)
        {
            var instanceContainer = applicationContainer.CreateChildContainer()
                    .RegisterType<INotificationService, NotificationService>(new ContainerControlledLifetimeManager())
                    .RegisterType(typeof(ILogger<>), new ContainerControlledLifetimeManager(), new InjectionFactory(
                        (factoryContainer, type, name) =>
                        {
                            var loggingTypeGenericArg = type.GetGenericArguments()[0];
                            var implType = typeof(LoggerImpl<>).MakeGenericType(loggingTypeGenericArg);
                            return implType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)
                                ?.Invoke(null, new object[0]);
                        }))
                    // First - registering channel to interact with telegram:
                    .RegisterType<ITelegramChannel, TelegramChannel>(new ContainerControlledLifetimeManager())

                    // Then setting up processors in chain of update processing responsibility:
                    // Starting from the last one:
                    .RegisterType<IUpdateProcessor, BotLogicUpdateProcessor>(
                        name: nameof(BotLogicUpdateProcessor),
                        lifetimeManager: new ContainerControlledLifetimeManager())

                    // Here we need to authenticate user:
                    .RegisterType<IUpdateProcessor>(
                        name: nameof(AuthenticationUpdateProcessor),
                        lifetimeManager: new ContainerControlledLifetimeManager(),
                        injectionMembers: new InjectionFactory(container =>
                            container.Resolve<AuthenticationUpdateProcessor>(
                                new DependencyOverride<IUpdateProcessor>(
                                    container.Resolve<IUpdateProcessor>(nameof(BotLogicUpdateProcessor))))))

                    // ... going towards the first throught PushProcessor:
                    .RegisterType<IUpdateProcessor>(
                        name: nameof(PushServiceCallbackUpdateProcessor),
                        lifetimeManager: new ContainerControlledLifetimeManager(),
                        injectionMembers:
                        new InjectionFactory(container =>
                            container.Resolve<PushServiceCallbackUpdateProcessor>(
                                new DependencyOverride<IUpdateProcessor>(
                                    container.Resolve<IUpdateProcessor>(nameof(AuthenticationUpdateProcessor))))))

                    // ... and towards with decorator used to draw "Typing..." while processing update:
                    .RegisterType<IUpdateProcessor>(
                        name: nameof(TypingDecoratorUpdateProcessor),
                        lifetimeManager: new ContainerControlledLifetimeManager(),
                        injectionMembers:
                        new InjectionFactory(container =>
                            container.Resolve<TypingDecoratorUpdateProcessor>(
                                new DependencyOverride<IUpdateProcessor>(
                                    container.Resolve<IUpdateProcessor>(nameof(PushServiceCallbackUpdateProcessor))))))

                    // ... processor for tracing
                    .RegisterType<IUpdateProcessor, UpdateTracingProcessor>(name: nameof(UpdateTracingProcessor),
                        lifetimeManager: new ContainerControlledLifetimeManager(), injectionMembers:
                        new InjectionConstructor(
                            new ResolvedParameter<Func<IBotDialogTraceBuilder>>(),
                            new ResolvedParameter<IUpdateProcessor>(nameof(TypingDecoratorUpdateProcessor)))
                    )

                    // For strongly-sequenced and providing mech for ingoring duplicate updates:
                    .RegisterType<IUpdateProcessor, UpdateSequenceAndDistinctProcessor>(
                        name: nameof(UpdateSequenceAndDistinctProcessor),
                        lifetimeManager: new ContainerControlledLifetimeManager(),
                        injectionMembers: new InjectionConstructor(
                            new ResolvedParameter<IUpdateProcessor>(nameof(UpdateTracingProcessor))))

                    // And the first one:

                    .RegisterType<IUpdateProcessor, PerChatSynchronizationProcessor>(name: null,
                        lifetimeManager: new ContainerControlledLifetimeManager(),
                        injectionMembers: new InjectionConstructor(
                            new ResolvedParameter<IUpdateProcessor>(nameof(UpdateSequenceAndDistinctProcessor))))


                    // Registering factory for creating per-chat instances
                    .RegisterType(typeof(IChatInstancesFactory<>), typeof(ChatInstancesFactory<>),
                        new ContainerControlledLifetimeManager())

                    // Unit Of Work:
                    .RegisterType<BotUnitOfWork>(new TransientLifetimeManager())
                    .RegisterType<IBotUnitOfWork, BotUnitOfWork>()


                    // Chat-independent services:
                    .RegisterType<ZuppEmployeesInfoService>(new ContainerControlledLifetimeManager())
                    .RegisterType<IPhonebookService, ZuppEmployeesInfoService>(new ContainerControlledLifetimeManager())
                    .RegisterType<IEmployeeHolidaysService, ZuppEmployeesInfoService>(
                        new ContainerControlledLifetimeManager())
                    .RegisterType<IEmployeeBusinessTripInfoSource, ZuppEmployeesInfoService>(
                        new ContainerControlledLifetimeManager())
                    .RegisterType<IEmployeesSourceService, ActiveDirectoryNTAccountsSourceImpl>(
                        new ContainerControlledLifetimeManager())
                    .RegisterType<ICarPassRequestService, BpmCarPassRequestServiceImpl>(
                        new ContainerControlledLifetimeManager())
                    .RegisterType<IGetListAvailableServicesService, BpmGetListAvailableServicesServiceImpl>(
                        new ContainerControlledLifetimeManager())
                    .RegisterType<IAddressListService, ActiveSyncAddressListServiceImpl>(
                        new ContainerControlledLifetimeManager())
                    .RegisterType<IPhonebookService, ZuppEmployeesInfoService>(new ContainerControlledLifetimeManager())
                    .RegisterType<IGuestWiFiCreationService, GuestWiFiCreationServiceImpl>(
                        new ContainerControlledLifetimeManager())
                    .RegisterType<IMailService, BotUnitOfWork>()
                    // Dummy contragent service

                    .RegisterType<IContragentSearchService, ContragentFromDislocationDistributionSearchService>(
                       new InjectionConstructor(new ResolvedParameter<IContragentSearchService>(nameof(ContragentHardCodedSearchService)), new ResolvedParameter<IBotConfiguration>()))
                    .RegisterType<IContragentSearchService, ContragentHardCodedSearchService>(nameof(ContragentHardCodedSearchService))
                    // Notification service:
                    .RegisterType<ITelegramBroadcastChannel, BroadcastChannelImpl>(new ContainerControlledLifetimeManager())

                    // Trace builder
                    .RegisterType<IBotDialogTraceBuilder, BotDialogTraceBuilderImpl>(new PerDialogLifetimeManager())

                    // Default inline receivers:
                    .RegisterType<ICallbackReceiverProcessFactory, WebHookInlineFactory>("WebHook")
                    .RegisterType<ICallbackReceiverProcessFactory, JiraHttpRequestInlineFactory>("JiraHttpRequest", new ContainerControlledLifetimeManager())
                    .RegisterType<ICallbackReceiverProcessFactory, SqlServerInlineFactory>("SQL Server Receiver", new ContainerControlledLifetimeManager())
                ;

            if (!instanceContainer.IsRegistered<TelegramBotConfigurationSection>())
            {
                return instanceContainer;
            }

            //var botConfig = instanceContainer.Resolve<TelegramBotConfigurationSection>();
            
            //var pluginsRegistrationConvention =
            //    new PluginModulesRegistrationConvention(botConfig.Plugins.Cast<PluginModule>());
            //instanceContainer.RegisterTypes(pluginsRegistrationConvention);

            return instanceContainer;
        }

        object IBootstrapper.Build(Type objectType) => _bootstrappingContainer.Resolve(objectType);
        #endregion

        #region Nested types
        private class ChatInstancesFactory<T> : IChatInstancesFactory<T>
        {
            #region Constructor
            public ChatInstancesFactory(IUnityContainer parentContainer)
            {
                _parentContainer = parentContainer;
                _chatContainers = new ConcurrentDictionary<Chat, IUnityContainer>();
            }
            #endregion

            #region Fields
            private readonly IUnityContainer _parentContainer;
            private readonly ConcurrentDictionary<Chat, IUnityContainer> _chatContainers;

            #endregion

            #region Methods
            T IChatInstancesFactory<T>.GetInstance(Chat chat) =>
                GetInstance(chat, null);

            public T GetInstance(Chat chat, string name) =>
                _chatContainers.GetOrAdd(chat,
                    CreateContainerForChat).Resolve<T>(name);


            private IUnityContainer CreateContainerForChat(Chat chat)
            {
                InjectionFactory CreateInjectionFactoryForStateMachine<TMachine>() where TMachine : ChatStateMachineBase => new InjectionFactory(container =>
                     container.Resolve<TMachine>(
                         new DependencyOverride<ITelegramChatChannel>(
                             container.Resolve<ITelegramChatChannel>(nameof(TracefullChatChannel))),
                         new ParameterOverride("instancesFactory",
                             container.Resolve<ContainerBasedInstancesFactory>(
                                 new ParameterOverride("container",
                                     container.CreateChildContainer()
                                         .RegisterType<ITelegramChatChannel, TracefullChatChannel>(
                                             new ContainerControlledLifetimeManager(),
                                             new InjectionConstructor(
                                                 container.Resolve<ITelegramChatChannel>(name: null),
                                                 container.Resolve<Func<IBotDialogTraceBuilder>>())))))
                     )
                );

                var chatContainer = _parentContainer.CreateChildContainer()
                        .RegisterInstance(chat)

                        // Bot conversation registration


                        // Chat channel
                        .RegisterType<ITelegramChatChannel, ChatChannelImpl>(new ContainerControlledLifetimeManager())
                        .RegisterType<ITelegramChatChannel, TracefullChatChannel>(nameof(TracefullChatChannel))

                        // Conversation logic - ChatStateMachine for employee
                        .RegisterType<IConversationLogic>(
                            name: "Employee",
                            lifetimeManager: new ContainerControlledLifetimeManager(),
                            injectionMembers: CreateInjectionFactoryForStateMachine<EmployeeChatStateMachine>()
                        )
                        // Conversation logic - contragent
                        .RegisterType<IConversationLogic>(name: "Contragent",
                            lifetimeManager: new ContainerControlledLifetimeManager(),
                            injectionMembers: CreateInjectionFactoryForStateMachine<ContragentChatStateMachine>()
                        )


                        // Chat-dependent services
                        .RegisterType<JiraServiceDeskService>(new ContainerControlledLifetimeManager())
                        .RegisterType<IServiceDeskCallCreationService, JiraServiceDeskService>()
                        .RegisterType<IInlineCallbackService, JiraServiceDeskService>(nameof(JiraServiceDeskService))
                        .RegisterType<IInlineCallbackService, ParkingsListSuggestingInlineService>(nameof(ParkingsListSuggestingInlineService), new ContainerControlledLifetimeManager())
                        .RegisterType<RequestListAvailableServicesTransition, RequestListAvailableServicesTransition>(nameof(RequestListAvailableServicesTransition), new ContainerControlledLifetimeManager())
                        .RegisterType<ICallbackMessageRedirectionService, CallbackMessageRedirectionServiceImpl>()
                        .RegisterType<IInlineCallbackService, CarDislocationWaitingForCarNumber>(
                            nameof(CarDislocationWaitingForCarNumber), new ContainerControlledLifetimeManager())
                    ;

                return chatContainer;
            }
            #endregion
        }
        private class PluginModulesRegistrationConvention : RegistrationConvention
        {
            #region Fields
            private readonly IEnumerable<PluginModule> _botConfigPlugins;
            #endregion

            #region Constructor
            public PluginModulesRegistrationConvention(IEnumerable<PluginModule> botConfigPlugins)
            {
                _botConfigPlugins = botConfigPlugins;
            }
            #endregion

            #region Methods
            public override IEnumerable<Type> GetTypes() => _botConfigPlugins.Select(module => module.Type);
            public override Func<Type, IEnumerable<Type>> GetFromTypes() => type =>
                new[] { _botConfigPlugins.First(module => module.Type == type).BaseType };
            public override Func<Type, string> GetName() =>
                type => _botConfigPlugins.First(module => module.Type == type).Name;
            public override Func<Type, LifetimeManager> GetLifetimeManager() => type => new ContainerControlledLifetimeManager();
            public override Func<Type, IEnumerable<InjectionMember>> GetInjectionMembers() => type => null;
            #endregion
        }
        #endregion
    }
}