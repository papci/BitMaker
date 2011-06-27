﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

using BitMaker.Utils;

using Jayrock.Json;
using Jayrock.Json.Conversion;

namespace BitMaker.Miner
{

    /// <summary>
    /// Hosts plugins and makes work available.
    /// </summary>
    public class MinerHost : IMinerContext
    {

        private object syncRoot = new object();

        /// <summary>
        /// Thread that starts execution of the miners.
        /// </summary>
        private Thread mainThread;

        /// <summary>
        /// Indicates that the host should stop.
        /// </summary>
        private CancellationTokenSource mainThreadCancel;

        /// <summary>
        /// Gets the current block number
        /// </summary>
        public uint CurrentBlockNumber { get; private set; }

        /// <summary>
        /// Milliseconds between refreshes of block number.
        /// </summary>
        private static int refreshPeriod = (int)TimeSpan.FromSeconds(15).TotalMilliseconds;

        /// <summary>
        /// Timer that pulls current block.
        /// </summary>
        private Timer refreshTimer;

        /// <summary>
        /// Milliseconds between recalculation of statistics.
        /// </summary>
        private static int statisticsPeriod = (int)TimeSpan.FromSeconds(3).TotalMilliseconds;

        /// <summary>
        /// Time the statistics were last calculated.
        /// </summary>
        private Stopwatch statisticsStopWatch;

        /// <summary>
        /// Timer that fires to collect statistics.
        /// </summary>
        private Timer statisticsTimer;

        /// <summary>
        /// Collects statistics about hash rate per second.
        /// </summary>
        private int hashesPerSecond;

        /// <summary>
        /// To report on hash generation rate.
        /// </summary>
        private int statisticsHashCount;

        /// <summary>
        /// Total hash count.
        /// </summary>
        private long hashCount;

        /// <summary>
        /// Available miner factories.
        /// </summary>
        [ImportMany]
        public IEnumerable<IMinerFactory> MinerFactories { get; set; }

        /// <summary>
        /// Currently executing miners.
        /// </summary>
        public List<MinerEntry> Miners { get; private set; }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public MinerHost()
        {
            // import available plugins
            new CompositionContainer(new DirectoryCatalog(".", "*.dll")).SatisfyImportsOnce(this);

            // default value
            Miners = new List<MinerEntry>();
            CurrentBlockNumber = 0;
        }

        /// <summary>
        /// Starts execution of the miners.
        /// </summary>
        public void Start()
        {
            if (mainThread != null)
                return;

            // recalculate statistics periodically
            mainThreadCancel = new CancellationTokenSource();
            mainThread = new Thread(MainThread);
            mainThread.Start();
        }

        /// <summary>
        /// Stops execution of the miner. Blocks until shutdown is complete.
        /// </summary>
        public void Stop()
        {
            if (mainThread == null)
                return;

            // indicates that any outstanding processes should stop
            mainThreadCancel.Cancel();
            mainThread.Join();
            mainThread = null;
        }

        /// <summary>
        /// Begins program execution on an independent thread so as not to block callers.
        /// </summary>
        private void MainThread()
        {
            try
            {
                // clear statistics
                hashesPerSecond = 0;
                statisticsHashCount = 0;
                hashCount = 0;

                // begin timer to compile statistics, even while testing miners
                statisticsTimer = new Timer(CalculateStatistics, null, 0, statisticsPeriod);
                statisticsStopWatch = new Stopwatch();

                // resources, with the miner factories that can use them
                var resourceSets = MinerFactories
                    .SelectMany(i => i.Resources.Select(j => new { Factory = i, Resource = j }))
                    .GroupBy(i => i.Resource)
                    .Select(i => new { Resource = i.Key, Factories = i.Select(j => j.Factory).ToList() })
                    .ToList();

                // for each resource, start the appropriate miner
                foreach (var resourceSet in resourceSets)
                {
                    IMinerFactory topFactory = null;
                    int topHashCount = 0;

                    if (resourceSet.Factories.Count == 1)
                        // only a single miner factory can consume this resource, so just use it
                        topFactory = resourceSet.Factories[0];
                    else
                    {
                        // multiple miners claim the ability to consume this resource
                        foreach (var factory in resourceSet.Factories)
                        {
                            if (mainThreadCancel.IsCancellationRequested)
                                return;

                            // sample the hash rate from the proposed miner
                            int hashes = SampleMiner(factory, resourceSet.Resource);
                            if (hashes > topHashCount)
                            {
                                topFactory = factory;
                                topHashCount = hashes;
                            }
                        }
                    }

                    if (mainThreadCancel.IsCancellationRequested)
                        return;

                    // start production miner
                    StartMiner(topFactory, resourceSet.Resource, this);
                }

                // periodically check latest block number
                refreshTimer = new Timer(RefreshBlock, null, 0, refreshPeriod);

                // wait until we're told to terminate
                lock (syncRoot)
                    while (!mainThreadCancel.IsCancellationRequested)
                        Monitor.Wait(syncRoot, 2000);
            }
            finally
            {
                // shut down each executing miner
                foreach (var i in Miners)
                {
                    i.Miner.Stop();
                    Miners.Remove(i);
                }

                // stop statistics
                if (statisticsTimer != null)
                {
                    statisticsTimer.Dispose();
                    statisticsTimer = null;
                }

                statisticsStopWatch = null;

                // stop block refresh
                if (refreshTimer != null)
                {
                    refreshTimer.Dispose();
                    refreshTimer = null;
                }
            }
        }

        /// <summary>
        /// Starts a new miner.
        /// </summary>
        /// <param name="factory"></param>
        /// <param name="resource"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        private IMiner StartMiner(IMinerFactory factory, MinerResource resource, IMinerContext context)
        {
            lock (syncRoot)
            {
                var miner = factory.StartMiner(context, resource);
                Miners.Add(new MinerEntry(miner, resource));
                return miner;
            }
        }

        /// <summary>
        /// Stops the given miner.
        /// </summary>
        /// <param name="miner"></param>
        private void StopMiner(IMiner miner)
        {
            lock (syncRoot)
            {
                var entry = Miners.Single(i => i.Miner == miner);
                miner.Stop();
                Miners.Remove(entry);
            }
        }

        /// <summary>
        /// Total number of hashes generated by miner.
        /// </summary>
        public long HashCount
        {
            get { return hashCount; }
        }

        /// <summary>
        /// Reports the average number of hashes being generated per-second.
        /// </summary>
        public int HashesPerSecond
        {
            get { return hashesPerSecond; }
        }

        /// <summary>
        /// Gets a new unit of work.
        /// </summary>
        /// <returns></returns>
        public Work GetWork(IMiner miner, string comment)
        {
            return Retry(GetWorkImpl, miner, comment, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Invokes the given function repeatidly, until it succeeds, with a delay.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="func"></param>
        /// <param name="delay"></param>
        /// <returns></returns>
        private R Retry<T1, T2, R>(Func<T1, T2, R> func, T1 arg0, T2 arg1, TimeSpan delay)
        {
            while (true)
                try
                {
                    return func(arg0, arg1);
                }
                catch (Exception)
                {
                    Thread.Sleep(delay);
                }
        }
        /// <summary>
        /// Invokes the given function repeatidly, until it succeeds, with a delay.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="func"></param>
        /// <param name="delay"></param>
        /// <returns></returns>
        private R Retry<T1, T2, T3, R>(Func<T1, T2, T3, R> func, T1 arg0, T2 arg1, T3 arg2, TimeSpan delay)
        {
            while (true)
                try
                {
                    return func(arg0, arg1, arg2);
                }
                catch (Exception)
                {
                    Thread.Sleep(delay);
                }
        }

        /// <summary>
        /// Accepts a completed unit of work and returns <c>true</c> if it is accepted.
        /// </summary>
        /// <param name="work"></param>
        /// <returns></returns>
        public bool SubmitWork(IMiner miner, Work work, string comment)
        {
            return Retry(SubmitWorkImpl, miner, work, comment, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Reports 
        /// </summary>
        /// <param name="plugin"></param>
        /// <param name="count"></param>
        public void ReportHashes(IMiner plugin, uint count)
        {
            Interlocked.Add(ref statisticsHashCount, (int)count);
        }

        /// <summary>
        /// Recalculates statistics.
        /// </summary>
        /// <param name="state"></param>
        private void CalculateStatistics(object state)
        {
            lock (syncRoot)
            {
                if (mainThread == null || mainThreadCancel.IsCancellationRequested)
                    return;

                // obtain current values, then restart watch
                statisticsStopWatch.Stop();
                var hc = Interlocked.Exchange(ref statisticsHashCount, 0);
                var dif = statisticsStopWatch.ElapsedMilliseconds;
                statisticsStopWatch.Restart();

                // add periodic count to total
                Interlocked.Add(ref hashCount, hc);

                if (dif > 1000)
                    hashesPerSecond = (int)(hc / (dif / 1000));
            }
        }

        /// <summary>
        /// Refreshes latest block number by simply retrieving new work.
        /// </summary>
        /// <param name="state"></param>
        private void RefreshBlock(object state)
        {
            try
            {
                GetWorkImpl(null, null);
            }
            catch (WebException)
            {
                // ignore
            }
        }

        #region JSON-RPC

        /// <summary>
        /// Generates a <see cref="T:HttpWebRequest"/> for communicating with the node's JSON-API.
        /// </summary>
        /// <returns></returns>
        private static HttpWebRequest RpcOpen(IMiner miner, string comment)
        {
            // configuration info, containing user name and password
            var url = ConfigurationSection.GetDefaultSection().Url;
            var user = url.UserInfo.Split(':');

            // create request, authenticating using information in the url
            var req = (HttpWebRequest)HttpWebRequest.Create(url);
            req.Credentials = new NetworkCredential(user[0], user[1]);
            req.PreAuthenticate = true;
            req.Method = "POST";
            req.Pipelined = true;
            req.UserAgent = "BitMaker";
            req.Headers["X-BitMaker-MachineName"] = Environment.MachineName;

            if (miner != null)
                req.Headers["X-BitMaker-Miner"] = miner.GetType().Name;

            if (!string.IsNullOrWhiteSpace(comment))
                req.Headers["X-BitMaker-Comment"] = comment;

            return req;
        }

        /// <summary>
        /// Invokes the 'getwork' JSON method and parses the result into a new <see cref="T:Work"/> instance.
        /// </summary>
        /// <returns></returns>
        private unsafe Work GetWorkImpl(IMiner miner, string comment)
        {
            var req = RpcOpen(miner, comment);

            // submit method invocation
            using (var txt = new StreamWriter(req.GetRequestStream()))
            using (var wrt = new JsonTextWriter(txt))
            {
                wrt.WriteStartObject();
                wrt.WriteMember("id");
                wrt.WriteString("json");
                wrt.WriteMember("method");
                wrt.WriteString("getwork");
                wrt.WriteMember("params");
                wrt.WriteStartArray();
                wrt.WriteEndArray();
                wrt.WriteEndObject();
                wrt.Flush();
            }

            var httpResponse = req.GetResponse();

            // obtain and update current block number
            uint blockNumber = 0;
            if (httpResponse.Headers["X-Blocknum"] != null)
                CurrentBlockNumber = blockNumber = uint.Parse(httpResponse.Headers["X-Blocknum"]);

            // retrieve invocation response
            using (var txt = new StreamReader(httpResponse.GetResponseStream()))
            using (var rdr = new JsonTextReader(txt))
            {
                if (!rdr.MoveToContent() && rdr.Read())
                    throw new JsonException("Unexpected content from 'getwork'.");

                var response = JsonConvert.Import<JsonGetWork>(rdr);
                if (response == null)
                    throw new JsonException("No response returned.");

                if (response.Error != null)
                    Console.WriteLine("JSON-RPC: {0}", response.Error);

                var result = response.Result;
                if (result == null)
                    return null;

                // decode data
                var data = Memory.Decode(result.Data);
                if (data.Length != 128)
                    throw new InvalidDataException("Received data is not valid.");

                // extract only header portion
                var header = new byte[80];
                Array.Copy(data, header, 80);

                var target = Memory.Decode(result.Target);
                if (target.Length != 32)
                    throw new InvalidDataException("Received target is not valid.");

                // generate new work instance
                var work = new Work()
                {
                    BlockNumber = blockNumber,
                    Header = header,
                    Target = target,
                };

                return work;
            }
        }

        /// <summary>
        /// Invokes the 'getwork' JSON method, submitting the proposed work. Returns <c>true</c> if the service accepts
        /// the proposed work.
        /// </summary>
        /// <param name="work"></param>
        /// <returns></returns>
        private static unsafe bool SubmitWorkImpl(IMiner miner, Work work, string comment)
        {
            var req = RpcOpen(miner, comment);

            // header needs to have SHA-256 padding appended
            var data = Sha256.AllocateInputBuffer(80);

            // prepare header buffer with SHA-256
            Sha256.Prepare(data, 80, 0);
            Sha256.Prepare(data, 80, 1);

            // dump header data on top of padding
            Array.Copy(work.Header, data, 80);

            // encode in proper format
            var solution = Memory.Encode(data);

            Console.WriteLine();
            Console.WriteLine("SOLUTION: {0,10} {1}", miner.GetType().Name, Memory.Encode(work.Header));
            Console.WriteLine();
            Console.WriteLine();

            using (var txt = new StreamWriter(req.GetRequestStream()))
            using (var wrt = new JsonTextWriter(txt))
            {
                wrt.WriteStartObject();
                wrt.WriteMember("id");
                wrt.WriteString("json");
                wrt.WriteMember("method");
                wrt.WriteString("getwork");
                wrt.WriteMember("params");
                wrt.WriteStartArray();
                wrt.WriteString(solution);
                wrt.WriteEndArray();
                wrt.WriteEndObject();
                wrt.Flush();
            }

            using (var txt = new StreamReader(req.GetResponse().GetResponseStream()))
            using (var rdr = new JsonTextReader(txt))
            {
                if (!rdr.MoveToContent() && rdr.Read())
                    throw new JsonException("Unexpected content from 'getwork <data>'.");

                var response = JsonConvert.Import<JsonSubmitWork>(rdr);
                if (response == null)
                    throw new JsonException("No response returned.");

                if (response.Error != null)
                    Console.WriteLine("JSON-RPC: {0}", response.Error);

                Console.WriteLine();
                Console.WriteLine("{0}: {1,10} {2}", response.Result ? "ACCEPTED" : "REJECTED", miner.GetType().Name, Memory.Encode(work.Header));
                Console.WriteLine();
                Console.WriteLine();

                return response.Result;
            }
        }

        #endregion

        /// <summary>
        /// Context to be used to sample the hash rate of a miner.
        /// </summary>
        private class SampleMinerContext : IMinerContext
        {

            private MinerHost host;

            internal int hashCount;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="host"></param>
            public SampleMinerContext(MinerHost host)
            {
                this.host = host;
            }

            public Work GetWork(IMiner miner, string comment)
            {
                return host.GetWork(miner, comment);
            }

            public bool SubmitWork(IMiner miner, Work work, string comment)
            {
                return host.SubmitWork(miner, work, comment);
            }

            public void ReportHashes(IMiner plugin, uint count)
            {
                // keep our own counter of hashes
                Interlocked.Add(ref hashCount, (int)count);

                // report to main host, these afterall do count for something
                host.ReportHashes(plugin, count);
            }

            public uint CurrentBlockNumber
            {
                get { return host.CurrentBlockNumber; }
            }

        }

        /// <summary>
        /// Starts a miner for a predetermined amount of time and records it's hash count.
        /// </summary>
        private int SampleMiner(IMinerFactory factory, MinerResource resource)
        {
            // context for sample run of miner
            var ctx = new SampleMinerContext(this);

            // begin the miner
            var miner = StartMiner(factory, resource, ctx);

            // allow the miner to work for a few seconds
            Thread.Sleep(5000);

            // pull the hash count immediately before stopping
            var hashCount = ctx.hashCount;
            StopMiner(miner);

            // return the hash count generated by the miner in the alloted time
            return hashCount;
        }

    }

}
