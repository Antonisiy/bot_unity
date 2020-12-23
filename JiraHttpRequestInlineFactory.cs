using System;
using System.Text;
using System.Web.Helpers;
using NTS.Telegram.DataAccessLayer;

namespace NTS.Telegram.Push.InlineReceivers.JiraHttpRequest
{
    public class JiraHttpRequestInlineFactory : ICallbackReceiverProcessFactory
    {
        #region Fields
        private readonly IChatInstancesFactory<ITelegramChatChannel> _chatChannelFactory;
        private readonly IBotConfiguration _configuration;
        private readonly ICallbackMessageRedirectionService _redirectionService;
        private readonly Func<IBotUnitOfWork> _getBotUnitOfWorkFunc;

        #endregion


        #region Constructor
        public JiraHttpRequestInlineFactory(IChatInstancesFactory<ITelegramChatChannel> chatChannelFactory, IBotConfiguration configuration, ICallbackMessageRedirectionService redirectionService, Func<IBotUnitOfWork> getBotUnitOfWorkFunc)
        {
            _chatChannelFactory = chatChannelFactory;
            _configuration = configuration;
            _redirectionService = redirectionService;
            _getBotUnitOfWorkFunc = getBotUnitOfWorkFunc;
        }
        #endregion

        #region Methods




        public ICallbackReceiverProcess CreateProcess(object configuration, object data)
        {
            return new JiraHttpRequestProcess(configuration as JiraHttpCallbackReceiverProcessConfiguration,
                data as byte[], _chatChannelFactory, _configuration.GetValue("AuthJiraSDToken"), _redirectionService, _getBotUnitOfWorkFunc);
        }

        public object ParseConfiguration(byte[] data)
        {
            var stringData = Encoding.Unicode.GetString(data);
            return Json.Decode<JiraHttpCallbackReceiverProcessConfiguration>(stringData);
        }

        public object ParseData(byte[] data) => data;
        #endregion
    }
}