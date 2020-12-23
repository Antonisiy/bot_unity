using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Web.Helpers;
using NTS.Telegram.DataAccessLayer;
using NTS.Telegram.Types;

namespace NTS.Telegram.Push.InlineReceivers.JiraHttpRequest
{
    public class JiraHttpRequestProcess : ICallbackReceiverProcess
    {
        #region Fields
        private readonly JiraHttpCallbackReceiverProcessConfiguration _configuration;
        private readonly byte[] _data;
        private readonly IChatInstancesFactory<ITelegramChatChannel> _chatChannelFactory;
        private readonly string _basicAuthBotToken;
        private readonly Func<IBotUnitOfWork> _getBotUnitOfWorkFunc;
        private ICallbackMessageRedirectionService _redirectionService;

        #endregion

        #region Ctor
        public JiraHttpRequestProcess(JiraHttpCallbackReceiverProcessConfiguration configuration, byte[] data, IChatInstancesFactory<ITelegramChatChannel> chatChannelFactory, string botToken, ICallbackMessageRedirectionService redirectionService, Func<IBotUnitOfWork> getBotUnitOfWorkFunc)
        {
            _configuration = configuration;
            _data = data;
            _chatChannelFactory = chatChannelFactory;
            _redirectionService = redirectionService;
            _getBotUnitOfWorkFunc = getBotUnitOfWorkFunc;
            _basicAuthBotToken = botToken;
        }
        #endregion

        #region Methods
        public CallbackReceiveResult ProcessCallback(Guid broadcastId, Update update, string callbackData)
        {
            var broadcastData = Encoding.UTF8.GetString(_data).Split('#');
            string issueKey = broadcastData[0], token = "";
            if (broadcastData.Length == 2)
            {
                token = broadcastData[1];
            }

            var isParse = Enum.TryParse<JiraCallbackType>(callbackData, out var enumKey);

            if (!isParse)
            {
                return CallbackReceiveResult.None;
            }

            switch (enumKey)
            {
                case JiraCallbackType.A:
                case JiraCallbackType.B:
                case JiraCallbackType.C:
                case JiraCallbackType.D:
                case JiraCallbackType.E:
                    {
                        var rating = enumKey switch
                        {
                            JiraCallbackType.A => 5,
                            JiraCallbackType.B => 4,
                            JiraCallbackType.C => 3,
                            JiraCallbackType.D => 2,
                            JiraCallbackType.E => 1,
                            _ => throw new ArgumentOutOfRangeException()
                        };
                        return SendFeedbackRating(update, _configuration.BaseUrl, issueKey, token, rating) ?
                                CallbackReceiveResult.RemoveButtons :
                                CallbackReceiveResult.None;
                    }
                case JiraCallbackType.ReopenIssue:
                    {
                        return SendReopenTransition(update, callbackData, _configuration.BaseUrl, issueKey) ?
                            CallbackReceiveResult.RemoveButtons :
                            CallbackReceiveResult.None;
                    }
                case JiraCallbackType.AdditionalInfo:
                    {
                        return SendAdditionalInfo(update, callbackData, _configuration.BaseUrl, issueKey) ?
                            CallbackReceiveResult.RemoveButtons :
                            CallbackReceiveResult.None;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private bool SendAdditionalInfo(Update update, string callbackData, string baseUrl, string issueKey)
        {
            var chatChannel = _chatChannelFactory.GetInstance(update.Chat);
            switch (update.Type)
            {
                case UpdateType.CallbackQueryUpdate:
                    _redirectionService.RegisterRedirection(
                        chatChannel.SendMessage("Введите, пожалуйста, ответ.\r\n\r\n<i>Пожалуйста, не убирайте ответ на это сообщение!</i>", true), update.CallbackQuery,
                        callbackData);
                    return false;
                case UpdateType.MessageUpdate:
                    {
                        if (!AddComment(update, baseUrl, issueKey))
                            throw new HttpRequestException($"Добавление комментария к заявке {issueKey} в JiraSD завершилось ошибкой!");

                        chatChannel.SendMessage("Спасибо, я приложу ваш комментарий к заявке!");
                        return true;
                    }
            }

            return false;
        }

        private bool SendReopenTransition(Update update, string callbackData, string baseUrl, string issueKey)
        {
            var chatChannel = _chatChannelFactory.GetInstance(update.Chat);
            switch (update.Type)
            {
                case UpdateType.CallbackQueryUpdate:
                    _redirectionService.RegisterRedirection(
                        chatChannel.SendMessage("Напишите,пожалуйста, почему вы хотите возобновить этот запрос?\r\n\r\n<i>Пожалуйста, не убирайте ответ на это сообщение!</i>", true), update.CallbackQuery,
                        callbackData);
                    return false;
                case UpdateType.MessageUpdate:
                    {
                        if (!AddComment(update, baseUrl, issueKey))
                            throw new HttpRequestException($"Добавление комментария к заявке {issueKey} в JiraSD завершилось ошибкой!");

                        using var httpClient = new HttpClient();
                        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(_basicAuthBotToken);

                        var payload = new ReopenIssuePayload { transition = new Transition { id = "161" } };
                        var url = baseUrl + $"/rest/api/2/issue/{issueKey}/transitions";

                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(Json.Encode(payload), Encoding.UTF8, "application/json")
                        };
                        using var responseMessage = httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseContentRead).Result;

                        if (!responseMessage.IsSuccessStatusCode)
                            throw new HttpRequestException("Возобновление запроса в JiraSD завершилось ошибкой!");

                        chatChannel.SendMessage("Понял, возобновил ваш запрос в Service Desk!");
                        return true;
                    }
            }

            return false;

        }

        private bool AddComment(Update update, string baseUrl, string issueKey)
        {
            using var uow = _getBotUnitOfWorkFunc();
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(_basicAuthBotToken);

            var session = uow.Sessions.Get(update.From.Id);
            var authorName = session?.NTUserName;
            if (string.IsNullOrEmpty(authorName))
            {
                throw new ArgumentNullException("AuthorName is null!");
            }

            var commentData = new CommentData { authorName = authorName, issueKey = issueKey, text = update.Message.Text };
            var stringJson = Json.Encode(commentData);
            var payload = new NewCommentPayload { data = Encoding.UTF8.GetBytes(stringJson) };
            var url = baseUrl + "/rest/scriptrunner/latest/custom/comment";

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(Json.Encode(payload), Encoding.UTF8, "application/json")
            };
            using var responseMessage = httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseContentRead).Result;

            return responseMessage.IsSuccessStatusCode;
        }

        private bool SendFeedbackRating(Update update, string baseUrl, string issueKey, string token, int rating)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(_basicAuthBotToken);
            var url = baseUrl + $"/servicedesk/customer/portal/4/{issueKey}/feedback?token={token}&rating={rating}";

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            using var responseMessage =
                httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseContentRead).Result;

            if (!responseMessage.IsSuccessStatusCode)
                throw new HttpRequestException("Оценка запроса в Jira SD завершилось ошибкой!");

            var chatChannel = _chatChannelFactory.GetInstance(update.Chat);
            chatChannel.SendMessage(
                $"Спасибо! Поставил ответственному за запрос оценку \"{GetTextMark(rating)}\"!");
            return true;

        }
        private static string GetTextMark(int mark)
        {
            return mark switch
            {
                1 => "очень плохо",
                2 => "плохо",
                3 => "ни хорошо, ни плохо",
                4 => "хорошо",
                5 => "очень хорошо",
                _ => ""
            };
        }
        #endregion

        #region NestedTypes
        public enum JiraCallbackType
        {
            A, B, C, D, E, ReopenIssue, AdditionalInfo
        }

        public class Transition
        {
            // ReSharper disable once InconsistentNaming
            public string id { get; set; }
        }
        public class ReopenIssuePayload
        {
            // ReSharper disable once InconsistentNaming
            public Transition transition { get; set; }
        }

        public class NewCommentPayload
        {
            public byte[] data { get; set; }
        }
        public class CommentData
        {

            public string authorName { get; set; }
            public string issueKey { get; set; }

            public string text { get; set; }

        }
        #endregion



    }
}