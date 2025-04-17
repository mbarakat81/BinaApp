using Binance.Net.Clients;
using Binance.Net.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Utilities;

namespace TradingBot
{
    public static class CoinSelector
    {
        public static async Task<string> SelectBestCoin(
            BinanceRestClient client,
            Dictionary<string, StrategyMetrics> strategyPerformance)
        {
            // First, get multiple potential candidates
            var topCoins = await SelectBestTradingPairs(client, strategyPerformance, 5);

            // Return the top candidate
            return topCoins.FirstOrDefault() ?? "BTCUSDT"; // Default to BTCUSDT if no coins found
        }

        public static async Task<List<string>> SelectBestTradingPairs(
            BinanceRestClient client,
            Dictionary<string, StrategyMetrics> strategyPerformance,
            int count)
        {
            // Fetch trading pairs with USDT as quote asset
            var exchangeInfo = await client.SpotApi.ExchangeData.GetExchangeInfoAsync();
            if (!exchangeInfo.Success)
            {
                throw new Exception("Failed to fetch exchange info: " + exchangeInfo.Error.Message);
            }

            // Filter for USDT pairs with sufficient status
            var usdtPairs = exchangeInfo.Data.Symbols
                .Where(s => s.QuoteAsset == "USDT" && s.Status == SymbolStatus.Trading)
                .ToList();

            // Get top coins by volume first
            var topVolumeSymbols = await GetTopCoinsByVolume(client, usdtPairs, Math.Min(100, count * 3));

            // Enhanced coin selection metrics
            var coinData = new List<CoinData>();

            // Process each coin in parallel to speed things up
            var tasks = topVolumeSymbols.Select(async symbol =>
            {
                try
                {
                    // Get hourly klines for past 48 hours
                    var klines = await client.SpotApi.ExchangeData.GetKlinesAsync(
                        symbol,
                        KlineInterval.OneHour,
                        null,
                        null,
                        48
                    );

                    if (!klines.Success || klines.Data.Count() < 24)
                        return null;

                    // Get 1-minute klines for more recent short-term analysis
                    var klines1m = await client.SpotApi.ExchangeData.GetKlinesAsync(
                        symbol,
                        KlineInterval.OneMinute,
                        null,
                        null,
                        60
                    );

                    if (!klines1m.Success)
                        return null;

                    // Calculate volatility (standard deviation of returns)
                    var hourlyCloses = klines.Data.Select(k => k.ClosePrice).ToList();
                    var minuteCloses = klines1m.Data.Select(k => k.ClosePrice).ToList();

                    var hourlyVolatility = IndicatorCalculator.CalculateVolatility(hourlyCloses);
                    var minuteVolatility = IndicatorCalculator.CalculateVolatility(minuteCloses);

                    // Get 24h ticker data
                    var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
                    if (!ticker.Success)
                        return null;

                    // Get order book to check liquidity depth
                    var orderBook = await client.SpotApi.ExchangeData.GetOrderBookAsync(symbol, 20);
                    if (!orderBook.Success)
                        return null;

                    // Calculate bid-ask spread
                    decimal spread = (orderBook.Data.Asks.FirstOrDefault().Price - orderBook.Data.Bids.FirstOrDefault().Price)
                        / orderBook.Data.Bids.FirstOrDefault().Price * 100;

                    // Calculate liquidity depth (sum of top 10 levels)
                    decimal bidDepth = orderBook.Data.Bids.Take(10).Sum(b => b.Quantity * b.Price);
                    decimal askDepth = orderBook.Data.Asks.Take(10).Sum(a => a.Quantity * a.Price);
                    decimal liquidityDepth = bidDepth + askDepth;

                    // Calculate momentum (rate of price change)
                    decimal momentum = IndicatorCalculator.CalculateMomentum(hourlyCloses, 4);

                    // Calculate correlation with BTC
                    var btcKlines = await client.SpotApi.ExchangeData.GetKlinesAsync(
                        "BTCUSDT",
                        KlineInterval.OneHour,
                        null,
                        null,
                        48
                    );

                    decimal correlation = 0;
                    if (btcKlines.Success && btcKlines.Data.Count() >= hourlyCloses.Count)
                    {
                        var btcCloses = btcKlines.Data.Select(k => k.ClosePrice).Take(hourlyCloses.Count).ToList();
                        correlation = IndicatorCalculator.CalculatePearsonCorrelation(hourlyCloses, btcCloses);
                    }

                    // Historical sentiment (performance recently)
                    decimal sentiment = ticker.Data.PriceChangePercent;

                    // Analyze trade frequency
                    var trades24h = ticker.Data.TotalTrades;

                    // Return coin data
                    return new CoinData
                    {
                        Symbol = symbol,
                        Volume = ticker.Data.QuoteVolume,
                        HourlyVolatility = hourlyVolatility,
                        MinuteVolatility = minuteVolatility,
                        BidAskSpread = spread,
                        LiquidityDepth = liquidityDepth,
                        Momentum = momentum,
                        BtcCorrelation = correlation,
                        Sentiment = sentiment,
                        TradeCount = trades24h,
                        PriceChangePercent = ticker.Data.PriceChangePercent
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error analyzing {symbol}: {ex.Message}");
                    return null;
                }
            }).ToList();

            // Wait for all tasks to complete
            await Task.WhenAll(tasks);

            // Add all successful results to our list
            foreach (var task in tasks)
            {
                var result1 = await task;
                if (result1 != null)
                {
                    coinData.Add(result1);
                }
            }

            // Calculate scoring for each coin based on all metrics
            decimal maxVolume = coinData.Count > 0 ? coinData.Max(c => c.Volume) : 1;
            decimal maxLiquidity = coinData.Count > 0 ? coinData.Max(c => c.LiquidityDepth) : 1;
            decimal maxSpread = coinData.Count > 0 ? coinData.Max(c => c.BidAskSpread) : 1;
            decimal maxVolatility = coinData.Count > 0 ? coinData.Max(c => c.HourlyVolatility) : 0.01m;
            decimal maxMomentum = coinData.Count > 0 ? coinData.Max(c => Math.Abs(c.Momentum)) : 0.01m;
            decimal maxTrades = coinData.Count > 0 ? coinData.Max(c => c.TradeCount) : 1;

            foreach (var coin in coinData)
            {
                // Convert all metrics to normalized 0-1 scores
                // Higher is better for volume and liquidity
                decimal normalizedVolume = coin.Volume / maxVolume;
                decimal normalizedLiquidity = coin.LiquidityDepth / maxLiquidity;

                // Lower is better for spread
                decimal normalizedSpread = maxSpread > 0 ? 1 - (coin.BidAskSpread / maxSpread) : 0.5m;

                // Middle values are best for volatility (not too high, not too low)
                decimal normalizedVolatility = maxVolatility > 0 ? coin.HourlyVolatility / maxVolatility : 0.5m;
                decimal volatilityScore = 1 - Math.Abs(normalizedVolatility - 0.6m);

                // Higher is better for momentum (in our scalping case)
                decimal normalizedMomentum = maxMomentum > 0 ? Math.Abs(coin.Momentum) / maxMomentum : 0.5m;

                // Middle correlation is best (not too correlated, not too independent)
                decimal correlationScore = 1 - Math.Abs(coin.BtcCorrelation - 0.5m) * 2;

                // Higher trade count is better
                decimal normalizedTrades = maxTrades > 0 ? coin.TradeCount / maxTrades : 0.5m;

                // Sentiment should be positive but not extreme
                decimal sentimentScore = 0;
                if (coin.Sentiment >= 0 && coin.Sentiment <= 5)
                    sentimentScore = coin.Sentiment / 5;
                else if (coin.Sentiment > 5)
                    sentimentScore = 1 - ((coin.Sentiment - 5) / 15); // Penalize extremely high values
                else
                    sentimentScore = 0; // Negative sentiment

                // Calculate final score with weighted metrics
                coin.Score = (normalizedVolume * 0.25m) + // Volume is very important
                             (normalizedLiquidity * 0.15m) + // Liquidity depth
                             (normalizedSpread * 0.10m) + // Tight spread
                             (volatilityScore * 0.20m) + // Good volatility for scalping
                             (normalizedMomentum * 0.10m) + // Momentum
                             (correlationScore * 0.05m) + // Correlation with BTC
                             (normalizedTrades * 0.05m) + // Trade frequency
                             (sentimentScore * 0.10m); // Recent performance
            }

            // Get historical performance data if available
            foreach (var coin in coinData)
            {
                if (strategyPerformance.ContainsKey(coin.Symbol))
                {
                    var metrics = strategyPerformance[coin.Symbol];
                    // Boost coins that have performed well historically
                    if (metrics.WinRate > 0.6m && metrics.AvgProfit > 0)
                    {
                        coin.Score *= 1 + (metrics.WinRate - 0.5m);
                        Console.WriteLine($"Boosted {coin.Symbol} score based on historical win rate of {metrics.WinRate:P2}");
                    }
                }
            }

            // Ensure diversity - try to avoid coins that are highly correlated with each other
            var diversifiedCoins = new List<CoinData>();
            var remainingCoins = new List<CoinData>(coinData.OrderByDescending(c => c.Score));

            while (diversifiedCoins.Count < count && remainingCoins.Count > 0)
            {
                var topCoin = remainingCoins.First();
                diversifiedCoins.Add(topCoin);
                remainingCoins.RemoveAt(0);

                // Remove coins that are highly correlated with the selected coin
                if (diversifiedCoins.Count < count)
                {
                    remainingCoins = remainingCoins
                        .Where(c => Math.Abs(c.BtcCorrelation - topCoin.BtcCorrelation) > 0.3m)
                        .ToList();
                }
            }

            // If we couldn't find enough diversified coins, just take the top scoring ones
            if (diversifiedCoins.Count < count)
            {
                diversifiedCoins = coinData.OrderByDescending(c => c.Score).Take(count).ToList();
            }

            var result = diversifiedCoins.Select(c => c.Symbol).ToList();

            // Always include BTC as a fallback option if we couldn't find enough coins
            if (result.Count < count && !result.Contains("BTCUSDT"))
            {
                result.Add("BTCUSDT");
            }

            Console.WriteLine("Selected top coins:");
            foreach (var coin in diversifiedCoins.Take(count))
            {
                Console.WriteLine($"- {coin.Symbol}: Score {coin.Score:F3}, " +
                                 $"Volume: ${coin.Volume:N0}, " +
                                 $"Volatility: {coin.HourlyVolatility:P2}");
            }

            return result;
        }

        private static async Task<List<string>> GetTopCoinsByVolume(
            BinanceRestClient client,
            List<Binance.Net.Objects.Models.Spot.BinanceSymbol> usdtPairs,
            int count)
        {
            // Get tickers for all pairs
            var tickers = await client.SpotApi.ExchangeData.GetTickersAsync();
            if (!tickers.Success)
            {
                throw new Exception("Failed to fetch tickers: " + tickers.Error.Message);
            }

            // Join with our USDT pairs
            var pairs = from pair in usdtPairs
                        join ticker in tickers.Data on pair.Name equals ticker.Symbol
                        where ticker.QuoteVolume > 1000000 // Only pairs with at least $1M volume
                        orderby ticker.QuoteVolume descending
                        select pair.Name;

            // Take top by volume
            return pairs.Take(count).ToList();
        }
    }

    // Helper class to store coin data for comparison
    public class CoinData
    {
        public string Symbol { get; set; }
        public decimal Volume { get; set; }
        public decimal HourlyVolatility { get; set; }
        public decimal MinuteVolatility { get; set; }
        public decimal BidAskSpread { get; set; }
        public decimal LiquidityDepth { get; set; }
        public decimal Momentum { get; set; }
        public decimal BtcCorrelation { get; set; }
        public decimal Sentiment { get; set; }
        public decimal TradeCount { get; set; }
        public decimal PriceChangePercent { get; set; }
        public decimal Score { get; set; }
    }
}
