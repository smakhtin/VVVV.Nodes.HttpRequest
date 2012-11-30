using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel.Composition;
using System.ComponentModel;

using System.Net;
using System.Threading;
using System.IO;
using System.Diagnostics;

using VVVV.PluginInterfaces.V2;
using VVVV.Core.Logging;

namespace VVVV.Nodes.HttpRequest
{
    public enum HttpRequestMethods
    {
        GET,
        POST,
        PUT,
        DELETE,
        HEAD,
        TRACE,
        OPTION
    }

    public enum HttpEncoding
    {
        ANSI,
        UTF8,
        ASCI
    }

    // This class stores the State of the request.
    public class RequestState
    {
        const int BufferSize = 1024;
        public byte[] BufferRead = new byte[BufferSize];

        public StringBuilder RequestData = new StringBuilder("");
        public HttpWebRequest Request = null;
        public HttpWebResponse Response = null;
        public Stream StreamResponse = null;

        public int SliceId = -1;
        public string Content = String.Empty;
        public string StatusMessage = String.Empty;
        public string Url = String.Empty;
        public string Accept = String.Empty;
        public HttpRequestMethods RequestMethod;
        public string ContentType = String.Empty;
        public int Timeout = 1000;
        public bool Aborted = false;

        public Dictionary<string, string> Headers = new Dictionary<string, string>(); 

        public ManualResetEvent ManualResetEvent = new ManualResetEvent(false);
        public List<Exception> Exceptions = new List<Exception>();
    }


    [PluginInfo(Name = "HTTP",
                Category = "Network",
                Version = "Request",
                Tags = "", Author = "phlegma",
                AutoEvaluate = false
               )]
    public class HttpRequestNode : IPluginEvaluate
    {
        #region vvvv declaration
        [Import()]
        ILogger FLogger;

        [Input("URL")]
        IDiffSpread<string> FUrlIn;

        [Input("Request")]
        IDiffSpread<bool> FSendRequestIn;

        [Input("Request Method", DefaultEnumEntry = "GET")]
        IDiffSpread<HttpRequestMethods> FRequestMethodIn;

        [Input("Content")]
        IDiffSpread<string> FContentIn;

        [Input("Content Type", DefaultString = "text/plain")]
        IDiffSpread<string> FContentTypeIn;

        [Input("Accept", DefaultString = "*/*")]
        IDiffSpread<string> FAcceptIn;

        [Input("Timeout", DefaultValue = 10000)]
        ISpread<int> FTimoutIn;

        [Input("Encoding", EnumName = "HttpEncoding")]
        IDiffSpread<EnumEntry> FEncoding;

        [Input("Header")] 
        private IDiffSpread<ISpread<string>> FHeaderIn;

        [Input("Header Value")] 
        private IDiffSpread<ISpread<string>> FHeaderValueIn;
        
        [Output("Response")]
        ISpread<string> FResponseOut;

        [Output("Statuscode")]
        ISpread<string> FStatusCodeOut;

        [Output("Fail", IsBang = true)]
        ISpread<bool> FFailOut;

        [Output("Success", IsBang = true)]
        ISpread<bool> FSuccessOut;

        [Output("Active")]
        ISpread<bool> FActiveOut;

        [Output("Elapse Time")]
        ISpread<double> FElaspeTimeOut;

        [ImportingConstructor]
        public HttpRequestNode()
        {
            var s = new[] { "Ansi", "Ascii", "UTF8", "UTF32", "Unicode" };
            EnumManager.UpdateEnum("HttpEncoding", "Ansi", s);
        }
        #endregion

        #region field declaration
        const int BufferSize = 1024;

        List<bool> FChangedSlice = new List<bool>();
        ISpread<string> FLastUrls;

        List<int> FSuccess = new List<int>();
        List<int> FFail = new List<int>();
        int FCounter = 0;

        Dictionary<int, string> FHttpStatusCode = new Dictionary<int, string>();
        Dictionary<int, RequestState> FRequestStateList = new Dictionary<int, RequestState>();
        Dictionary<int, BackgroundWorker> FThreadList = new Dictionary<int, BackgroundWorker>();
        #endregion

        #region IPluginEvaluate Members
        public void Evaluate(int spreadMax)
        {
            FResponseOut.SliceCount = spreadMax;
            FFailOut.SliceCount = spreadMax;
            FSuccessOut.SliceCount = spreadMax;
            FStatusCodeOut.SliceCount = spreadMax;
            FActiveOut.SliceCount = spreadMax;
            FElaspeTimeOut.SliceCount = spreadMax;


            #region Make a async request

            if (FSendRequestIn.IsChanged)
            {
                for (int i = 0; i < spreadMax; i++)
                {
                    if (FSendRequestIn[i])
                    {

                        if (FThreadList.ContainsKey(i))
                        {

                            BackgroundWorker worker;
                            RequestState requestStateDelete;
                            FThreadList.TryGetValue(i, out worker);
                            FRequestStateList.TryGetValue(i, out requestStateDelete);

                            if (requestStateDelete != null)
                            {
                                requestStateDelete.Request.Abort();
                                requestStateDelete.ManualResetEvent.Set();
                            }
                            if (worker != null) worker.CancelAsync();

                            FThreadList.Remove(i);
                            FRequestStateList.Remove(i);
                        }

                        RequestState requestState;
                        using (var worker = new BackgroundWorker())
                        {
                            worker.DoWork += HandlerDoWork;
                            worker.RunWorkerCompleted += HandlerRunWorkerCompleted;

                            worker.WorkerSupportsCancellation = true;

                            requestState = new RequestState
                                {
                                    Accept = FAcceptIn[i],
                                    Content = FContentIn[i],
                                    RequestMethod = FRequestMethodIn[i],
                                    ContentType = FContentTypeIn[i],
                                    Url = FUrlIn[i],
                                    Timeout = FTimoutIn[i],
                                    SliceId = i
                                };

                            var headers = FHeaderIn[i];
                            var headersValues = FHeaderValueIn[i];

                            for (int j = 0; j < headers.SliceCount; j++)
                            {
                                requestState.Headers.Add(headers[i], headersValues[i]); 
                            }

                            worker.RunWorkerAsync(requestState);
                            FActiveOut[i] = true;
                            FThreadList.Add(i, worker);
                        }

                        FRequestStateList.Add(i, requestState);
                    }
                }
            }

            #endregion

            for (int i = 0; i < spreadMax; i++)
            {
                FSuccessOut[i] = false;
                FFailOut[i] = false;
            }
        }


        #region Backrounworker Handlers

        void HandlerRunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            var requestState = e.Result as RequestState;

            if (e.Cancelled)
            {

                FLogger.Log(LogType.Message, "Request was cancelled");
            }
            else if (e.Error != null)
            {
                FLogger.Log(e.Error);

            }
            else
            {
                Debug.WriteLine(e.Result.ToString());
                //RequestState RequestState = e.Result as RequestState;

                if (requestState != null)
                {
                    var sliceId = requestState.SliceId;


                    if (requestState.Exceptions.Count > 0)
                    {
                        foreach (var ex in requestState.Exceptions)
                        {
                            FLogger.Log(LogType.Error, ex.Message);

                        }
                        requestState.Exceptions.Clear();
                        FFailOut[sliceId] = true;
                    }
                    else
                    {
                        if (requestState.RequestData.Length > 0)
                            FResponseOut[sliceId] = requestState.RequestData.ToString();
                        FSuccessOut[sliceId] = true;
                    }

                    try
                    {
                        FStatusCodeOut[sliceId] = requestState.Response.StatusCode.ToString();
                    }
                    catch (NullReferenceException)
                    {
                        FFailOut[sliceId] = true;
                        FLogger.Log(LogType.Error, "No Response received");
                    }

                    FActiveOut[sliceId] = false;
                }
            }
        }

        void HandlerDoWork(object sender, DoWorkEventArgs e)
        {
            var worker = sender as BackgroundWorker;
            var requestState = e.Argument as RequestState;

            if (worker != null && worker.CancellationPending)
            {
                e.Cancel = true;
            }
            else
            {
                if (requestState != null)
                {
                    var index = requestState.SliceId;

                    var uri = new Uri(requestState.Url);
                    var request = (HttpWebRequest)WebRequest.Create(uri);
                    request.Method = requestState.RequestMethod.ToString();
                    request.ContentType = requestState.ContentType;
                    request.Accept = requestState.Accept;

                    foreach (var keyValuePair in requestState.Headers)
                    {
                        request.Headers[keyValuePair.Key] = keyValuePair.Value;
                    }

                    requestState.Request = request;
                    IAsyncResult result = null;

                    switch (requestState.RequestMethod)
                    {
                        case HttpRequestMethods.GET:
                            result = request.BeginGetResponse(ResponseCallback, requestState);
                            break;

                        case HttpRequestMethods.POST:

                            request.ContentLength = FContentIn[index].Length;
                            result = request.BeginGetRequestStream(GetRequestStreamCallback, requestState);
                            break;

                        case HttpRequestMethods.HEAD:
                            result = request.BeginGetResponse(ResponseCallback, requestState);
                            break;

                        case HttpRequestMethods.PUT:
                            request.ContentLength = FContentIn[index].Length;
                            result = request.BeginGetRequestStream(GetRequestStreamCallback, requestState);
                            break;

                        case HttpRequestMethods.DELETE:
                            result = request.BeginGetResponse(ResponseCallback, requestState);
                            break;

                        case HttpRequestMethods.TRACE:
                            result = request.BeginGetResponse(ResponseCallback, requestState);
                            break;

                        case HttpRequestMethods.OPTION:
                            result = request.BeginGetResponse(ResponseCallback, requestState);
                            break;
                    }

                    if (result != null)
                        ThreadPool.RegisterWaitForSingleObject(result.AsyncWaitHandle, TimeoutCallback, requestState, requestState.Timeout, true);

                    if (!requestState.Aborted)
                    {
                        requestState.ManualResetEvent.WaitOne();
                    }

                    if (requestState.Request.HaveResponse)
                    {
                        requestState.Response.Close();
                    }
                }

                e.Result = requestState;
            }

        }


        #endregion


        private void GetRequestStreamCallback(IAsyncResult result)
        {
            var requestState = (RequestState)result.AsyncState;

            try
            {
                var request = requestState.Request;
                // End the operation
                var postStream = request.EndGetRequestStream(result);


                // Convert the string into a byte array.
                var byteArray = Encoding.Default.GetBytes(requestState.Content);

                // Write to the request stream.
                postStream.Write(byteArray, 0, requestState.Content.Length);
                postStream.Close();

                // Start the asynchronous operation to get the response
                request.BeginGetResponse(ResponseCallback, requestState);
            }
            catch (WebException e)
            {
                requestState.Exceptions.Add(e);
            }

            requestState.ManualResetEvent.Set();
        }



        private void ResponseCallback(IAsyncResult result)
        {
            var requestState = (RequestState)result.AsyncState;

            try
            {
                // State of request is asynchronous.

                var httpWebRequest = requestState.Request;
                requestState.Response = (HttpWebResponse)httpWebRequest.EndGetResponse(result);

                // Read the response into a Stream object.
                var responseStream = requestState.Response.GetResponseStream();
                requestState.StreamResponse = responseStream;

                // Begin the Reading of the contents of the HTML page and print it to the console.
                if (responseStream != null)
                {
                    responseStream.BeginRead(requestState.BufferRead, 0, BufferSize, ReadCallBack, requestState);
                }
                return;
            }
            catch (WebException e)
            {
                requestState.Exceptions.Add(e);
            }

            requestState.ManualResetEvent.Set();
        }


        private void ReadCallBack(IAsyncResult asyncResult)
        {
            var requestState = (RequestState)asyncResult.AsyncState;
            var slice = requestState.SliceId;

            try
            {

                var responseStream = requestState.StreamResponse;

                var read = responseStream.EndRead(asyncResult);
                // Read the HTML page and then print it to the console.
                if (read > 0)
                {
                    switch (FEncoding[slice].Index)
                    {
                        case (0):
                            requestState.RequestData.Append(Encoding.Default.GetString(requestState.BufferRead, 0, read));
                            break;
                        case (1):
                            requestState.RequestData.Append(Encoding.ASCII.GetString(requestState.BufferRead, 0, read));
                            break;
                        case (2):
                            requestState.RequestData.Append(Encoding.UTF8.GetString(requestState.BufferRead, 0, read));
                            break;
                        case (3):
                            requestState.RequestData.Append(Encoding.UTF32.GetString(requestState.BufferRead, 0, read));
                            break;
                        case (4):
                            requestState.RequestData.Append(Encoding.Unicode.GetString(requestState.BufferRead, 0, read));
                            break;
                        default:
                            requestState.RequestData.Append(Encoding.Default.GetString(requestState.BufferRead, 0, read));
                            break;
                    }

                    responseStream.BeginRead(requestState.BufferRead, 0, BufferSize, ReadCallBack, requestState);
                    return;
                }
                if (requestState.RequestData.Length >= 0)
                {
                    responseStream.Close();

                }
            }
            catch (WebException e)
            {
                requestState.Exceptions.Add(e);
            }
            catch (ObjectDisposedException ex)
            {
                requestState.Exceptions.Add(ex);
            }

            requestState.ManualResetEvent.Set();
        }


        // Abort the request if the timer fires.
        private void TimeoutCallback(object state, bool timedOut)
        {
            if (!timedOut) return;
            var requestState = state as RequestState;

            if (requestState != null) requestState.Aborted = true;
        }

    }
        #endregion
}