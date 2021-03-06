﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBXplorer;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer
{
    public class DerivationSchemeParser
    {
        public Network Network { get; set; }
        public Script HintScriptPubKey { get; set; }
        static Dictionary<uint, string[]> electrumMapping;

        static DerivationSchemeParser()
        {
            //Source https://github.com/spesmilo/electrum/blob/11733d6bc271646a00b69ff07657119598874da4/electrum/constants.py
            electrumMapping = new Dictionary<uint, string[]>();
            electrumMapping.Add(0x0488b21eU, new[] { "legacy" });
            electrumMapping.Add(0x049d7cb2U, new string[] { "p2sh" });
            electrumMapping.Add(0x4b24746U, Array.Empty<string>());
        }
        public DerivationSchemeParser(Network expectedNetwork)
        {
            Network = expectedNetwork;
        }



        public DerivationStrategyBase ParseElectrum(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));
            str = str.Trim();
            var data = Network.GetBase58CheckEncoder().DecodeData(str);
            if (data.Length < 4)
                throw new FormatException();
            var prefix = Utils.ToUInt32(data, false);

            var standardPrefix = Utils.ToBytes(0x0488b21eU, false);
            for (int ii = 0; ii < 4; ii++)
                data[ii] = standardPrefix[ii];
            var extPubKey = new BitcoinExtPubKey(Network.GetBase58CheckEncoder().EncodeData(data), Network.Main).ToNetwork(Network);
            if (!electrumMapping.TryGetValue(prefix, out string[] labels))
            {
                throw new FormatException();
            }
            if (labels.Length == 0)
                return new DirectDerivationStrategy(extPubKey) { Segwit = true };
            if (labels[0] == "legacy")
                return new DirectDerivationStrategy(extPubKey) { Segwit = false };
            if (labels[0] == "p2sh") // segwit p2sh
                return new DerivationStrategyFactory(Network).Parse(extPubKey.ToString() + "-[p2sh]");
            throw new FormatException();
        }


        public DerivationStrategyBase Parse(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));
            str = str.Trim();

            HashSet<string> hintedLabels = new HashSet<string>();

            var hintDestination = HintScriptPubKey?.GetDestination();
            if (hintDestination != null)
            {
                if (hintDestination is KeyId)
                {
                    hintedLabels.Add("legacy");
                }
                if (hintDestination is ScriptId)
                {
                    hintedLabels.Add("p2sh");
                }
            }

            if (!Network.Consensus.SupportSegwit)
                hintedLabels.Add("legacy");

            try
            {
                var result = new DerivationStrategyFactory(Network).Parse(str);
                return FindMatch(hintedLabels, result);
            }
            catch
            {
            }

            var parts = str.Split('-');
            bool hasLabel = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (IsLabel(parts[i]))
                {
                    if (!hasLabel)
                    {
                        hintedLabels.Clear();
                        if (!Network.Consensus.SupportSegwit)
                            hintedLabels.Add("legacy");
                    }
                    hasLabel = true;
                    hintedLabels.Add(parts[i].Substring(1, parts[i].Length - 2).ToLowerInvariant());
                    continue;
                }
                try
                {
                    var data = Network.GetBase58CheckEncoder().DecodeData(parts[i]);
                    if (data.Length < 4)
                        continue;
                    var prefix = Utils.ToUInt32(data, false);

                    var standardPrefix = Utils.ToBytes(0x0488b21eU, false);
                    for (int ii = 0; ii < 4; ii++)
                        data[ii] = standardPrefix[ii];
                    var derivationScheme = new BitcoinExtPubKey(Network.GetBase58CheckEncoder().EncodeData(data), Network.Main).ToNetwork(Network).ToString();

                    electrumMapping.TryGetValue(prefix, out string[] labels);
                    if (labels != null)
                    {
                        foreach (var label in labels)
                        {
                            hintedLabels.Add(label.ToLowerInvariant());
                        }
                    }
                    parts[i] = derivationScheme;
                }
                catch { continue; }
            }

            if (hintDestination != null)
            {
                if (hintDestination is WitKeyId)
                {
                    hintedLabels.Remove("legacy");
                    hintedLabels.Remove("p2sh");
                }
            }

            str = string.Join('-', parts.Where(p => !IsLabel(p)));
            foreach (var label in hintedLabels)
            {
                str = $"{str}-[{label}]";
            }

            return FindMatch(hintedLabels, new DerivationStrategyFactory(Network).Parse(str));
        }

        private DerivationStrategyBase FindMatch(HashSet<string> hintLabels, DerivationStrategyBase result)
        {
            var facto = new DerivationStrategyFactory(Network);
            var firstKeyPath = new KeyPath("0/0");
            if (HintScriptPubKey == null)
                return result;
            if (HintScriptPubKey == result.Derive(firstKeyPath).ScriptPubKey)
                return result;

            if (result is MultisigDerivationStrategy)
                hintLabels.Add("keeporder");

            var resultNoLabels = result.ToString();
            resultNoLabels = string.Join('-', resultNoLabels.Split('-').Where(p => !IsLabel(p)));
            foreach (var labels in ItemCombinations(hintLabels.ToList()))
            {
                var hinted = facto.Parse(resultNoLabels + '-' + string.Join('-', labels.Select(l => $"[{l}]").ToArray()));
                if (HintScriptPubKey == hinted.Derive(firstKeyPath).ScriptPubKey)
                    return hinted;
            }
            throw new FormatException("Could not find any match");
        }

        private static bool IsLabel(string v)
        {
            return v.StartsWith('[') && v.EndsWith(']');
        }

        /// <summary>
        /// Method to create lists containing possible combinations of an input list of items. This is
        /// basically copied from code by user "jaolho" on this thread:
        /// http://stackoverflow.com/questions/7802822/all-possible-combinations-of-a-list-of-values
        /// </summary>
        /// <typeparam name="T">type of the items on the input list</typeparam>
        /// <param name="inputList">list of items</param>
        /// <param name="minimumItems">minimum number of items wanted in the generated combinations,
        ///                            if zero the empty combination is included,
        ///                            default is one</param>
        /// <param name="maximumItems">maximum number of items wanted in the generated combinations,
        ///                            default is no maximum limit</param>
        /// <returns>list of lists for possible combinations of the input items</returns>
        public static List<List<T>> ItemCombinations<T>(List<T> inputList, int minimumItems = 1,
                                                int maximumItems = int.MaxValue)
        {
            int nonEmptyCombinations = (int)Math.Pow(2, inputList.Count) - 1;
            List<List<T>> listOfLists = new List<List<T>>(nonEmptyCombinations + 1);

            if (minimumItems == 0)  // Optimize default case
                listOfLists.Add(new List<T>());

            for (int i = 1; i <= nonEmptyCombinations; i++)
            {
                List<T> thisCombination = new List<T>(inputList.Count);
                for (int j = 0; j < inputList.Count; j++)
                {
                    if ((i >> j & 1) == 1)
                        thisCombination.Add(inputList[j]);
                }

                if (thisCombination.Count >= minimumItems && thisCombination.Count <= maximumItems)
                    listOfLists.Add(thisCombination);
            }

            return listOfLists;
        }
    }
}
