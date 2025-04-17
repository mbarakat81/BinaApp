using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot.Analyzers
{
    public static class PriceLevelAnalyzer
    {
        public static async Task<List<KeyPriceLevel>> IdentifyKeyPriceLevels(
            BinanceRestClient client,
            string symbol)
        {
            var result = new List<KeyPriceLevel>();

            try
            {
                // Get price data from multiple timeframes
                var klines1h = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.OneHour,
                    null,
                    null,
                    100
                );

                var klines15m = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FifteenMinutes,
                    null,
                    null,
                    100
                );

                if (!klines1h.Success || !klines15m.Success)
                    return result;

                // Identify support levels
                var supportLevels = IdentifySupportLevels(klines1h.Data.ToList(), klines15m.Data.ToList());
                var resistanceLevels = IdentifyResistanceLevels(klines1h.Data.ToList(), klines15m.Data.ToList());

                // Combine and sort by strength
                result.AddRange(supportLevels);
                result.AddRange(resistanceLevels);
                result = result.OrderByDescending(l => l.Strength).ToList();

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error identifying key price levels: {ex.Message}");
                return result;
            }
        }

        public static List<KeyPriceLevel> IdentifySupportLevels(
            List<IBinanceKline> klines1h,
            List<IBinanceKline> klines15m)
        {
            var supportLevels = new List<KeyPriceLevel>();
            var recentLows = new List<decimal>();

            // Find local minimums from hourly data
            for (int i = 3; i < klines1h.Count - 3; i++)
            {
                if (klines1h[i].LowPrice < klines1h[i - 1].LowPrice &&
                    klines1h[i].LowPrice < klines1h[i - 2].LowPrice &&
                    klines1h[i].LowPrice < klines1h[i - 3].LowPrice &&
                    klines1h[i].LowPrice < klines1h[i + 1].LowPrice &&
                    klines1h[i].LowPrice < klines1h[i + 2].LowPrice &&
                    klines1h[i].LowPrice < klines1h[i + 3].LowPrice)
                {
                    recentLows.Add(klines1h[i].LowPrice);
                }
            }

            // Find local minimums from 15m data for more recent levels
            for (int i = 3; i < klines15m.Count - 3; i++)
            {
                if (klines15m[i].LowPrice < klines15m[i - 1].LowPrice &&
                    klines15m[i].LowPrice < klines15m[i - 2].LowPrice &&
                    klines15m[i].LowPrice < klines15m[i - 3].LowPrice &&
                    klines15m[i].LowPrice < klines15m[i + 1].LowPrice &&
                    klines15m[i].LowPrice < klines15m[i + 2].LowPrice &&
                    klines15m[i].LowPrice < klines15m[i + 3].LowPrice)
                {
                    recentLows.Add(klines15m[i].LowPrice);
                }
            }

            // Group similar price levels (within 0.1% of each other)
            var groupedLevels = new Dictionary<decimal, List<decimal>>();

            foreach (var low in recentLows)
            {
                bool added = false;
                foreach (var level in groupedLevels.Keys)
                {
                    if (Math.Abs(low - level) / level <= 0.001m)
                    {
                        groupedLevels[level].Add(low);
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    groupedLevels[low] = new List<decimal> { low };
                }
            }

            // Convert groups to price levels with strength
            foreach (var group in groupedLevels)
            {
                var avgPrice = group.Value.Average();
                supportLevels.Add(new KeyPriceLevel
                {
                    Price = avgPrice,
                    Strength = group.Value.Count,
                    Type = "Support"
                });
            }

            return supportLevels.OrderByDescending(l => l.Strength).Take(5).ToList();
        }

        public static List<KeyPriceLevel> IdentifyResistanceLevels(
            List<IBinanceKline> klines1h,
            List<IBinanceKline> klines15m)
        {
            var resistanceLevels = new List<KeyPriceLevel>();
            var recentHighs = new List<decimal>();

            // Find local maximums from hourly data
            for (int i = 3; i < klines1h.Count - 3; i++)
            {
                if (klines1h[i].HighPrice > klines1h[i - 1].HighPrice &&
                    klines1h[i].HighPrice > klines1h[i - 2].HighPrice &&
                    klines1h[i].HighPrice > klines1h[i - 3].HighPrice &&
                    klines1h[i].HighPrice > klines1h[i + 1].HighPrice &&
                    klines1h[i].HighPrice > klines1h[i + 2].HighPrice &&
                    klines1h[i].HighPrice > klines1h[i + 3].HighPrice)
                {
                    recentHighs.Add(klines1h[i].HighPrice);
                }
            }

            // Find local maximums from 15m data for more recent levels
            for (int i = 3; i < klines15m.Count - 3; i++)
            {
                if (klines15m[i].HighPrice > klines15m[i - 1].HighPrice &&
                    klines15m[i].HighPrice > klines15m[i - 2].HighPrice &&
                    klines15m[i].HighPrice > klines15m[i - 3].HighPrice &&
                    klines15m[i].HighPrice > klines15m[i + 1].HighPrice &&
                    klines15m[i].HighPrice > klines15m[i + 2].HighPrice &&
                    klines15m[i].HighPrice > klines15m[i + 3].HighPrice)
                {
                    recentHighs.Add(klines15m[i].HighPrice);
                }
            }

            // Group similar price levels (within 0.1% of each other)
            var groupedLevels = new Dictionary<decimal, List<decimal>>();

            foreach (var high in recentHighs)
            {
                bool added = false;
                foreach (var level in groupedLevels.Keys)
                {
                    if (Math.Abs(high - level) / level <= 0.001m)
                    {
                        groupedLevels[level].Add(high);
                        added = true;
                        break;
                    }
                }

                if (!added)
                {
                    groupedLevels[high] = new List<decimal> { high };
                }
            }

            // Convert groups to price levels with strength
            foreach (var group in groupedLevels)
            {
                var avgPrice = group.Value.Average();
                resistanceLevels.Add(new KeyPriceLevel
                {
                    Price = avgPrice,
                    Strength = group.Value.Count,
                    Type = "Resistance"
                });
            }

            return resistanceLevels.OrderByDescending(l => l.Strength).Take(5).ToList();
        }
    }
}
