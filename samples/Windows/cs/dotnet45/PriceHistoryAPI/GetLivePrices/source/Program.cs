/* Copyright 2019 FXCM Global Services, LLC

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use these files except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System;
using System.Collections.Specialized;
using System.Text;
using System.Configuration;
using System.Globalization;

using fxcore2;
using Candleworks.PriceHistoryMgr;

namespace GetLivePrices
{
    // NOTE: the example doesn't handle the session reconnecting event.
    class Program
    {
        static void Main(string[] args)
        {
            O2GSession session = null;
            IPriceHistoryCommunicator communicator = null;
            SessionStatusListener statusListener = null;
            bool loggedIn = false;

            try
            {
                LoginParams loginParams = new LoginParams(ConfigurationManager.AppSettings);
                SampleParams sampleParams = new SampleParams(ConfigurationManager.AppSettings);

                PrintSampleParams("GetLivePrices", loginParams, sampleParams);

                // use the application module path as a base path for quotes storage
                string storagePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "History");

                // create the ForexConnect trading session
                session = O2GTransport.createSession();
                statusListener = new SessionStatusListener(session, loginParams.SessionID, loginParams.Pin);
                // subscribe IO2GSessionStatus interface implementation for the status events
                session.subscribeSessionStatus(statusListener);
                statusListener.Reset();

                // create an instance of IPriceHistoryCommunicator
                communicator = PriceHistoryCommunicatorFactory.createCommunicator(session, storagePath);

                // log in to ForexConnect
                session.login(loginParams.Login, loginParams.Password, loginParams.URL, loginParams.Connection);
                if (statusListener.WaitEvents() && statusListener.Connected)
                {
                    loggedIn = true;

                    CommunicatorStatusListener communicatorStatusListener = new CommunicatorStatusListener();
                    communicator.addStatusListener(communicatorStatusListener);

                    // wait until the communicator signals that it is ready
                    if (communicator.isReady() ||
                        (communicatorStatusListener.WaitEvents() && communicatorStatusListener.Ready))
                    {
                        // attach the instance of the class that implements the IPriceHistoryCommunicatorListener
                        // interface to the communicator
                        ResponseListener responseListener = new ResponseListener();
                        communicator.addListener(responseListener);

                        GetLivePrices(communicator, sampleParams.Instrument, sampleParams.Timeframe, 
                            sampleParams.DateFrom, sampleParams.DateTo, sampleParams.QuotesCount, responseListener, 
                            session, statusListener);
                        Console.WriteLine("Done!");

                        communicator.removeListener(responseListener);
                    }

                    communicator.removeStatusListener(communicatorStatusListener);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: {0}", e.ToString());
            }
            finally
            {
                if (communicator != null)
                {
                    communicator.Dispose();
                }
                if (session != null)
                {
                    if (loggedIn)
                    {
                        try
                        {
                            statusListener.Reset();
                            session.logout();
                            statusListener.WaitEvents();
                        }
                        catch (Exception ee)
                        {
                        }
                    }

                    session.unsubscribeSessionStatus(statusListener);
                    session.Dispose();
                }
            }
        }

        /// <summary>
        /// Request historical prices for the specified timeframe of the specified period
        /// and then show live prices.
        /// </summary>
        /// <param name="communicator">The price history communicator.</param>
        /// <param name="instrument">The instrument.</param>
        /// <param name="timeframe">The timeframe.</param>
        /// <param name="from">From-date.</param>
        /// <param name="to">To-date</param>
        /// <param name="quotesCount">The quotes count.</param>
        /// <param name="responseListener">The response listener.</param>
        /// <param name="session">The trading session.</param>
        /// <param name="sessionListener">The trading session listener.</param>
        public static void GetLivePrices(IPriceHistoryCommunicator communicator, string instrument, string timeframe, 
                                         DateTime from, DateTime to, int quotesCount, ResponseListener responseListener, 
                                         O2GSession session, SessionStatusListener sessionListener)
        {
            if (!communicator.isReady())
            {
                Console.WriteLine("History communicator is not ready.");
                return;
            }

            // create timeframe entity
            ITimeframeFactory timeframeFactory = communicator.TimeframeFactory;
            O2GTimeframe timeframeObj = timeframeFactory.create(timeframe);

            // check timeframe for ticks
            if (O2GTimeframeUnit.Tick == timeframeObj.Unit)
                throw new Exception("Application works only for bars. Don't use tick as timeframe.");

            // load Offers table and start ticks listening
            PriceUpdateController priceUpdateController = new PriceUpdateController(session, instrument);
            if (!priceUpdateController.Wait())
                return;

            // create period collection
            bool alive = true;
            PeriodCollection periods = new PeriodCollection(instrument, timeframe, alive, priceUpdateController);

            PeriodCollectionUpdateObserver livePriceViewer = new PeriodCollectionUpdateObserver(periods);

            // create and send a history request
            IPriceHistoryCommunicatorRequest request = communicator.createRequest(instrument, timeframeObj, from, to, quotesCount);
            responseListener.SetRequest(request);
            communicator.sendRequest(request);

            // wait results
            responseListener.Wait();

            IPriceHistoryCommunicatorResponse response = responseListener.GetResponse();
            O2GMarketDataSnapshotResponseReader reader = communicator.createResponseReader(response);

            if (response != null)
                ProcessHistoricalPrices(communicator, response, ref periods);

            // finally notify the collection that all bars are added, so it can
            // add all ticks collected while the request was being executed
            // and start update the data by forthcoming ticks
            periods.Finish(reader.getLastBarTime(), reader.getLastBarVolume());

            // continue update the data until cancelled by a user
            Console.WriteLine("\nPress ENTER to cancel.\n\n");
            Console.ReadKey();

            livePriceViewer.Unsubscribe();
            priceUpdateController.Unsubscribe();
        }

        /// <summary>
        /// Print history data from response and fills periods collection.
        /// </summary>
        /// <param name="communicator">The price history communicator.</param>
        /// <param name="response">The response. Cannot be null.</param>
        /// <param name="periods">The periods collection.</param>
        public static void ProcessHistoricalPrices(IPriceHistoryCommunicator communicator, IPriceHistoryCommunicatorResponse response, ref PeriodCollection periods)
        {
            // use O2GMarketDataSnapshotResponseReader to extract price data from the response object 
            O2GMarketDataSnapshotResponseReader reader = communicator.createResponseReader(response);
            for (int i = 0; i < reader.Count; i++)
            {
                if (reader.isBar)
                {
                    periods.Add(reader.getDate(i), reader.getBidOpen(i), reader.getBidHigh(i), reader.getBidLow(i), reader.getBidClose(i),
                        reader.getAskOpen(i), reader.getAskHigh(i), reader.getAskLow(i), reader.getAskClose(i), reader.getVolume(i));

                    Console.WriteLine("DateTime={0}, BidOpen={1}, BidHigh={2}, BidLow={3}, BidClose={4}, AskOpen={5}, AskHigh={6}, AskLow={7}, AskClose={8}, Volume={9}",
                        reader.getDate(i), reader.getBidOpen(i), reader.getBidHigh(i), reader.getBidLow(i), reader.getBidClose(i),
                        reader.getAskOpen(i), reader.getAskHigh(i), reader.getAskLow(i), reader.getAskClose(i), reader.getVolume(i));
                }
            }
        }

        private static void PrintSampleParams(string procName, LoginParams loginPrm, SampleParams prm)
        {
            Console.WriteLine("{0}: Instrument='{1}', Timeframe='{2}', DateFrom='{3}', QuotesCount='{4}'",
                procName, prm.Instrument, prm.Timeframe, prm.DateFrom.ToString("MM.dd.yyyy HH:mm:ss"), prm.QuotesCount);
        }

        class LoginParams
        {
            public string Login
            {
                get
                {
                    return mLogin;
                }
            }
            private string mLogin;

            public string Password
            {
                get
                {
                    return mPassword;
                }
            }
            private string mPassword;

            public string URL
            {
                get
                {
                    return mURL;
                }
            }
            private string mURL;

            public string Connection
            {
                get
                {
                    return mConnection;
                }
            }
            private string mConnection;

            public string SessionID
            {
                get
                {
                    return mSessionID;
                }
            }
            private string mSessionID;

            public string Pin
            {
                get
                {
                    return mPin;
                }
            }
            private string mPin;

            public LoginParams(NameValueCollection args)
            {
                mLogin = GetRequiredArgument(args, "Login");
                mPassword = GetRequiredArgument(args, "Password");
                mURL = GetRequiredArgument(args, "URL");
                if (!string.IsNullOrEmpty(mURL))
                {
                    if (!mURL.EndsWith("Hosts.jsp", StringComparison.OrdinalIgnoreCase))
                    {
                        mURL += "/Hosts.jsp";
                    }
                }
                mConnection = GetRequiredArgument(args, "Connection");
                mSessionID = args["SessionID"];
                mPin = args["Pin"];
            }

            /// <summary>
            /// Get required argument from configuration file
            /// </summary>
            /// <param name="args">Configuration file key-value collection</param>
            /// <param name="sArgumentName">Argument name (key) from configuration file</param>
            /// <returns>Argument value</returns>
            private string GetRequiredArgument(NameValueCollection args, string sArgumentName)
            {
                string sArgument = args[sArgumentName];
                if (!string.IsNullOrEmpty(sArgument))
                {
                    sArgument = sArgument.Trim();
                }
                if (string.IsNullOrEmpty(sArgument))
                {
                    throw new Exception(string.Format("Please provide {0} in configuration file", sArgumentName));
                }
                return sArgument;
            }
        }

        class SampleParams
        {
            public string Instrument
            {
                get
                {
                    return mInstrument;
                }
            }
            private string mInstrument;

            public string Timeframe
            {
                get
                {
                    return mTimeframe;
                }
            }
            private string mTimeframe;

            public DateTime DateFrom
            {
                get
                {
                    return mDateFrom;
                }
            }
            private DateTime mDateFrom;

            public DateTime DateTo
            {
                get
                {
                    return mDateTo;
                }
            }
            private DateTime mDateTo;

            public int QuotesCount
            {
                get
                {
                    return mQuotesCount;
                }
            }
            private int mQuotesCount;

            public SampleParams(NameValueCollection args)
            {
                string sDateFormat = "MM.dd.yyyy HH:mm:ss";
                mInstrument = GetRequiredArgument(args, "Instrument");
                mTimeframe = GetRequiredArgument(args, "Timeframe");

                string sDateFrom = args["DateFrom"];
                bool bIsDateFromNotSpecified = false;
                if (!DateTime.TryParseExact(sDateFrom, sDateFormat, CultureInfo.InvariantCulture, 
                           DateTimeStyles.None, out mDateFrom))
                {
                    bIsDateFromNotSpecified = true;
                    mDateFrom = Candleworks.PriceHistoryMgr.Constants.ZERODATE; // ZERODATE
                }
                else
                {
                    if (DateTime.Compare(mDateFrom, DateTime.UtcNow) >= 0)
                    {
                        throw new Exception(string.Format("Sorry, \"DateFrom\" value {0} should be in the past; " +
                            "please fix the value in the configuration file", sDateFrom));
                    }
                }

                // till now
                mDateTo = Candleworks.PriceHistoryMgr.Constants.ZERODATE; // ZERODATE

                string sQuotesCount = args["Count"];
                if (!Int32.TryParse(sQuotesCount, out mQuotesCount))
                    mQuotesCount = -1;
                else if (mQuotesCount <= 0)
                    mQuotesCount = -1;
            }

            /// <summary>
            /// Get required argument from configuration file
            /// </summary>
            /// <param name="args">Configuration file key-value collection</param>
            /// <param name="sArgumentName">Argument name (key) from configuration file</param>
            /// <returns>Argument value</returns>
            private string GetRequiredArgument(NameValueCollection args, string sArgumentName)
            {
                string sArgument = args[sArgumentName];
                if (!string.IsNullOrEmpty(sArgument))
                {
                    sArgument = sArgument.Trim();
                }
                if (string.IsNullOrEmpty(sArgument))
                {
                    throw new Exception(string.Format("Please provide {0} in configuration file", sArgumentName));
                }
                return sArgument;
            }
        }
    }
}
