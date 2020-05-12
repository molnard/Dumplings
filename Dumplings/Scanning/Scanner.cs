﻿using Dumplings.Analysis;
using Dumplings.Helpers;
using Dumplings.Rpc;
using Microsoft.Extensions.Caching.Memory;
using MoreLinq;
using NBitcoin;
using NBitcoin.RPC;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dumplings.Scanning
{
    public class Scanner
    {
        public Scanner(RPCClient rpc)
        {
            Rpc = rpc;
            Directory.CreateDirectory(WorkFolder);
        }

        public const string WorkFolder = "Scanner";
        public static readonly string LastProcessedBlockHeightPath = Path.Combine(WorkFolder, "LastProcessedBlockHeight.txt");
        public static readonly string WasabiCoinJoinsPath = Path.Combine(WorkFolder, "WasabiCoinJoins.txt");
        public static readonly string SamouraiCoinJoinsPath = Path.Combine(WorkFolder, "SamouraiCoinJoins.txt");
        public static readonly string SamouraiTx0sPath = Path.Combine(WorkFolder, "SamouraiTx0s.txt");
        public static readonly string OtherCoinJoinsPath = Path.Combine(WorkFolder, "OtherCoinJoins.txt");
        public static readonly string WasabiPostMixTxsPath = Path.Combine(WorkFolder, "WasabiPostMixTxs.txt");
        public static readonly string SamouraiPostMixTxsPath = Path.Combine(WorkFolder, "SamouraiPostMixTxs.txt");
        public static readonly string OtherCoinJoinPostMixTxsPath = Path.Combine(WorkFolder, "OtherCoinJoinPostMixTxs.txt");

        public RPCClient Rpc { get; }

        private decimal PercentageDone { get; set; } = 0;
        private decimal PreviousPercentageDone { get; set; } = -1;

        public async Task ScanAsync(bool rescan)
        {
            if (rescan)
            {
                Logger.LogWarning("Rescanning...");
            }
            if (rescan && Directory.Exists(WorkFolder))
            {
                Directory.Delete(WorkFolder, true);
            }
            Directory.CreateDirectory(WorkFolder);
            var allWasabiCoinJoinSet = new HashSet<uint256>();
            var allSamouraiCoinJoinSet = new HashSet<uint256>();
            var allOtherCoinJoinSet = new HashSet<uint256>();
            var allSamouraiTx0Set = new HashSet<uint256>();

            var opreturnTransactionCache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 100000 });

            ulong startingHeight = Constants.FirstWasabiBlock;
            ulong height = startingHeight;
            if (File.Exists(LastProcessedBlockHeightPath))
            {
                height = ulong.Parse(File.ReadAllText(LastProcessedBlockHeightPath)) + 1;
                allSamouraiCoinJoinSet = Enumerable.ToHashSet(File.ReadAllLines(SamouraiCoinJoinsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x).Id));
                allWasabiCoinJoinSet = Enumerable.ToHashSet(File.ReadAllLines(WasabiCoinJoinsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x).Id));
                allOtherCoinJoinSet = Enumerable.ToHashSet(File.ReadAllLines(OtherCoinJoinsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x).Id));
                allSamouraiTx0Set = Enumerable.ToHashSet(File.ReadAllLines(SamouraiTx0sPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x).Id));
                Logger.LogWarning($"{height - startingHeight + 1} blocks already processed. Continue scanning...");
            }

            var bestHeight = (ulong)await Rpc.GetBlockCountAsync().ConfigureAwait(false);

            Logger.LogInfo($"Last processed block: {height - 1}.");
            ulong totalBlocks = bestHeight - height + 1;
            Logger.LogInfo($"About {totalBlocks} ({totalBlocks / 144} days) blocks will be processed.");

            var stopWatch = new Stopwatch();
            var processedBlocksWhenSwStarted = CalculateProcessedBlocks(height, bestHeight, totalBlocks);
            stopWatch.Start();

            while (height <= bestHeight)
            {
                var block = await Rpc.GetVerboseBlockAsync(height).ConfigureAwait(false);

                var wasabiCoinJoins = new List<VerboseTransactionInfo>();
                var samouraiCoinJoins = new List<VerboseTransactionInfo>();
                var samouraiTx0s = new List<VerboseTransactionInfo>();
                var otherCoinJoins = new List<VerboseTransactionInfo>();
                var wasabiPostMixTxs = new List<VerboseTransactionInfo>();
                var samouraiPostMixTxs = new List<VerboseTransactionInfo>();
                var otherCoinJoinPostMixTxs = new List<VerboseTransactionInfo>();

                foreach (var tx in block.Transactions)
                {
                    if (tx.Outputs.Count() > 2 && tx.Outputs.Any(x => TxNullDataTemplate.Instance.CheckScriptPubKey(x.ScriptPubKey)))
                    {
                        opreturnTransactionCache.Set(tx.Id, tx, new MemoryCacheEntryOptions().SetSize(1));
                    }

                    bool isWasabiCj = false;
                    bool isSamouraiCj = false;
                    bool isOtherCj = false;
                    var indistinguishableOutputs = tx.GetIndistinguishableOutputs(includeSingle: false).ToArray();
                    if (tx.Inputs.All(x => x.Coinbase is null) && indistinguishableOutputs.Any())
                    {
                        var outputs = tx.Outputs.ToArray();
                        var inputs = tx.Inputs.Select(x => x.PrevOutput).ToArray();
                        var outputValues = outputs.Select(x => x.Value);
                        var inputValues = inputs.Select(x => x.Value);
                        var outputCount = outputs.Length;
                        var inputCount = inputs.Length;
                        (Money mostFrequentEqualOutputValue, int mostFrequentEqualOutputCount) = indistinguishableOutputs.MaxBy(x => x.count);
                        // IDENTIFY WASABI COINJOINS
                        if (block.Height >= Constants.FirstWasabiBlock)
                        {
                            // Before Wasabi had constant coordinator addresses and different base denominations at the beginning.
                            if (block.Height < Constants.FirstWasabiNoCoordAddressBlock)
                            {
                                isWasabiCj = tx.Outputs.Any(x => Constants.WasabiCoordScripts.Contains(x.ScriptPubKey)) && indistinguishableOutputs.Any(x => x.count > 2);
                            }
                            else
                            {
                                isWasabiCj =
                                    mostFrequentEqualOutputCount >= 10 // At least 10 equal outputs.
                                    && inputCount >= mostFrequentEqualOutputCount // More inptu than outputs.
                                    && mostFrequentEqualOutputValue.Almost(Constants.ApproximateWasabiBaseDenomination, Constants.WasabiBaseDenominationPrecision); // The most frequent equal outputs must be almost the base denomination.
                            }
                        }

                        // IDENTIFY SAMOURAI COINJOINS
                        if (block.Height >= Constants.FirstSamouraiBlock)
                        {
                            isSamouraiCj =
                               inputCount == 5 // Always have 5 inputs.
                               && outputCount == 5 // Always have 5 outputs.
                               && outputValues.Distinct().Count() == 1 // Outputs are always equal.
                               && Constants.SamouraiPools.Any(x => x.Almost(tx.Outputs.First().Value, Money.Coins(0.01m))); // Just to be sure match Samourai's pool sizes.
                        }

                        // IDENTIFY OTHER EQUAL OUTPUT COINJOIN LIKE TRANSACTIONS
                        if (!isWasabiCj && !isSamouraiCj)
                        {
                            isOtherCj =
                                indistinguishableOutputs.Length == 1 // If it isn't then it'd be likely a multidenomination CJ, which only Wasabi does.                                                                 
                                && mostFrequentEqualOutputCount == outputCount - mostFrequentEqualOutputCount // Rarely it isn't, but it helps filtering out false positives.
                                && outputs.Select(x => x.ScriptPubKey).Distinct().Count() >= mostFrequentEqualOutputCount // Otherwise more participants would be single actors which makes no sense.
                                && inputs.Select(x => x.ScriptPubKey).Distinct().Count() >= mostFrequentEqualOutputCount // Otherwise more participants would be single actors which makes no sense.
                                && inputValues.Max() <= mostFrequentEqualOutputValue + outputValues.Where(x => x != mostFrequentEqualOutputValue).Max() - Money.Coins(0.0001m); // I don't want to run expensive subset sum, so this is a shortcut to at least filter out false positives.
                        }

                        if (isWasabiCj)
                        {
                            wasabiCoinJoins.Add(tx);
                            allWasabiCoinJoinSet.Add(tx.Id);
                        }
                        else if (isSamouraiCj)
                        {
                            samouraiCoinJoins.Add(tx);
                            allSamouraiCoinJoinSet.Add(tx.Id);
                        }
                        else if (isOtherCj)
                        {
                            otherCoinJoins.Add(tx);
                            allOtherCoinJoinSet.Add(tx.Id);
                        }

                        foreach (var txid in tx.Inputs.Select(x => x.OutPoint.Hash))
                        {
                            if (!isWasabiCj && allWasabiCoinJoinSet.Contains(txid) && !wasabiPostMixTxs.Any(x => x.Id == txid))
                            {
                                // Then it's a post mix tx.
                                wasabiPostMixTxs.Add(tx);
                            }

                            if (!isSamouraiCj && allSamouraiCoinJoinSet.Contains(txid) && !samouraiPostMixTxs.Any(x => x.Id == txid))
                            {
                                // Then it's a post mix tx.
                                samouraiPostMixTxs.Add(tx);
                            }

                            if (!isOtherCj && allOtherCoinJoinSet.Contains(txid) && !otherCoinJoinPostMixTxs.Any(x => x.Id == txid))
                            {
                                // Then it's a post mix tx.
                                otherCoinJoinPostMixTxs.Add(tx);
                            }
                        }
                    }
                }

                foreach (var txid in samouraiCoinJoins.SelectMany(x => x.Inputs).Select(x => x.OutPoint.Hash).Where(x => !allSamouraiCoinJoinSet.Contains(x) && !allSamouraiTx0Set.Contains(x)).Distinct())
                {
                    if (!opreturnTransactionCache.TryGetValue(txid, out VerboseTransactionInfo vtxi))
                    {
                        var tx0Candidate = await Rpc.GetSmartRawTransactionInfoAsync(txid).ConfigureAwait(false);
                        if (tx0Candidate.Transaction.Outputs.Any(x => TxNullDataTemplate.Instance.CheckScriptPubKey(x.ScriptPubKey)))
                        {
                            var verboseOutputs = new List<VerboseOutputInfo>(tx0Candidate.Transaction.Outputs.Count);
                            foreach (var o in tx0Candidate.Transaction.Outputs)
                            {
                                var voi = new VerboseOutputInfo(o.Value, o.ScriptPubKey);
                                verboseOutputs.Add(voi);
                            }

                            var verboseInputs = new List<VerboseInputInfo>(tx0Candidate.Transaction.Inputs.Count);
                            foreach (var i in tx0Candidate.Transaction.Inputs)
                            {
                                var tx = await Rpc.GetRawTransactionAsync(i.PrevOut.Hash).ConfigureAwait(false);
                                var o = tx.Outputs[i.PrevOut.N];
                                var voi = new VerboseOutputInfo(o.Value, o.ScriptPubKey);
                                var vii = new VerboseInputInfo(i.PrevOut, voi);
                                verboseInputs.Add(vii);
                            }

                            vtxi = new VerboseTransactionInfo(tx0Candidate.TransactionBlockInfo, txid, verboseInputs, verboseOutputs);
                        }

                        if (vtxi is { })
                        {
                            allSamouraiTx0Set.Add(txid);
                            samouraiTx0s.Add(vtxi);
                        }
                    }
                }

                decimal totalBlocksPer100 = totalBlocks / 100m;
                ulong processedBlocks = CalculateProcessedBlocks(height, bestHeight, totalBlocks);
                PercentageDone = processedBlocks / totalBlocksPer100;
                bool displayProgress = (PercentageDone - PreviousPercentageDone) >= 0.1m;
                if (displayProgress)
                {
                    var blocksWithinElapsed = processedBlocks - processedBlocksWhenSwStarted;
                    ulong blocksLeft = bestHeight - height;
                    var elapsed = stopWatch.Elapsed;

                    if (blocksWithinElapsed != 0)
                    {
                        var estimatedTimeLeft = (elapsed / blocksWithinElapsed) * blocksLeft;

                        Logger.LogInfo($"Progress: {PercentageDone:0.#}%, Current height: {height}, Estimated time left: {estimatedTimeLeft.TotalHours:0.#} hours.");

                        PreviousPercentageDone = PercentageDone;

                        processedBlocksWhenSwStarted = processedBlocks;
                        stopWatch.Restart();
                    }
                }
                if (bestHeight <= height)
                {
                    // Refresh bestHeight and if still no new block, then end here.
                    bestHeight = (ulong)await Rpc.GetBlockCountAsync().ConfigureAwait(false);
                    if (bestHeight <= height)
                    {
                        break;
                    }
                }

                File.WriteAllText(LastProcessedBlockHeightPath, height.ToString());
                File.AppendAllLines(WasabiCoinJoinsPath, wasabiCoinJoins.Select(x => RpcParser.ToLine(x)));
                File.AppendAllLines(SamouraiCoinJoinsPath, samouraiCoinJoins.Select(x => RpcParser.ToLine(x)));
                File.AppendAllLines(SamouraiTx0sPath, samouraiTx0s.Select(x => RpcParser.ToLine(x)));
                File.AppendAllLines(OtherCoinJoinsPath, otherCoinJoins.Select(x => RpcParser.ToLine(x)));
                File.AppendAllLines(WasabiPostMixTxsPath, wasabiPostMixTxs.Select(x => RpcParser.ToLine(x)));
                File.AppendAllLines(SamouraiPostMixTxsPath, samouraiPostMixTxs.Select(x => RpcParser.ToLine(x)));
                File.AppendAllLines(OtherCoinJoinPostMixTxsPath, otherCoinJoinPostMixTxs.Select(x => RpcParser.ToLine(x)));

                height++;
            }
        }


        private static ulong CalculateProcessedBlocks(ulong height, ulong bestHeight, ulong totalBlocks)
        {
            ulong blocksLeft = bestHeight - height;
            ulong processedBlocks = totalBlocks - blocksLeft;
            return processedBlocks;
        }
    }
}