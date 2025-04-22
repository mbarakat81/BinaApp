using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Utilities;

namespace TradingBot.Analyzers
{
    public static class MarketAnalyzer
    {
        public static async Task<MarketCondition> AnalyzeMarketCondition(
            BinanceRestClient client,
            string symbol)
        {
            var condition = new MarketCondition();

            try
            {
                // Check multiple timeframes for the selected symbol
                var klines1h = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.OneHour,
                    null,
                    null,
                    48
                );

                var klines15m = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FifteenMinutes,
                    null,
                    null,
                    48
                );

                var klines5m = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FiveMinutes,
                    null,
                    null,
                    48
                );

                var klines1m = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.OneMinute,
                    null,
                    null,
                    60
                );

                if (!klines1h.Success || !klines15m.Success || !klines5m.Success || !klines1m.Success)
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Failed to get price data across multiple timeframes";
                    return condition;
                }

                // Extract price data
                var closes1h = klines1h.Data.Select(k => k.ClosePrice).ToList();
                var closes15m = klines15m.Data.Select(k => k.ClosePrice).ToList();
                var closes5m = klines5m.Data.Select(k => k.ClosePrice).ToList();
                var closes1m = klines1m.Data.Select(k => k.ClosePrice).ToList();

                var volumes1h = klines1h.Data.Select(k => k.Volume).ToList();
                var volumes15m = klines15m.Data.Select(k => k.Volume).ToList();
                var volumes5m = klines5m.Data.Select(k => k.Volume).ToList();

                // Calculate EMAs for trend detection
                var ema9_1h = IndicatorCalculator.CalculateEMA(closes1h, 9);
                var ema21_1h = IndicatorCalculator.CalculateEMA(closes1h, 21);
                var ema50_1h = IndicatorCalculator.CalculateEMA(closes1h, 50);

                var ema9_15m = IndicatorCalculator.CalculateEMA(closes15m, 9);
                var ema21_15m = IndicatorCalculator.CalculateEMA(closes15m, 21);

                var ema9_5m = IndicatorCalculator.CalculateEMA(closes5m, 9);
                var ema21_5m = IndicatorCalculator.CalculateEMA(closes5m, 21);

                // Calculate RSI
                condition.RSIVolatility = IndicatorCalculator.CalculateRSI(closes5m, 14);

                // Calculate MACD
                var (macdLine, signalLine) = IndicatorCalculator.CalculateMACD(closes5m);
                condition.MacdBullish = macdLine > signalLine;

                // Calculate volatility
                decimal volatility1h = IndicatorCalculator.CalculateVolatility(closes1h);
                decimal volatility5m = IndicatorCalculator.CalculateVolatility(closes5m);
                condition.IsVolatile = volatility5m > 0.003m; // Consider volatile if 5m volatility > 0.3%

                // Calculate ATR for measuring strong trends
                decimal atr1h = IndicatorCalculator.CalculateATR(klines1h.Data.ToList(), 14);
                decimal atrPercent = atr1h / closes1h.Last() * 100;
                condition.IsStrongTrend = atrPercent > 0.5m; // Strong trend if ATR > 0.5% of price

                // Check volume trend - higher bar for increasing volume (was 1:1, now 1.2:1)
                bool increasingVolume = volumes5m.Skip(volumes5m.Count / 2).Average() >
                                        volumes5m.Take(volumes5m.Count / 2).Average() * 1.2m;

                // Identify key price levels
                condition.SupportLevels = PriceLevelAnalyzer.IdentifySupportLevels(klines1h.Data.ToList(), klines15m.Data.ToList());
                condition.ResistanceLevels = PriceLevelAnalyzer.IdentifyResistanceLevels(klines1h.Data.ToList(), klines15m.Data.ToList());

                // Determine trend direction using multiple indicators
                bool uptrend1h = ema9_1h > ema21_1h && ema21_1h > ema50_1h;
                bool downtrend1h = ema9_1h < ema21_1h && ema21_1h < ema50_1h;
                bool uptrend5m = ema9_5m > ema21_5m;
                bool downtrend5m = ema9_5m < ema21_5m;

                // Check for price momentum in the last few minutes
                decimal recentPriceChange = (closes1m.Last() - closes1m[closes1m.Count - 6]) / closes1m[closes1m.Count - 6] * 100;
                // Reduced momentum threshold for entry
                bool recentMomentum = recentPriceChange > 0.08m; // Reduced from 0.12% to 0.08%


                if (uptrend1h && uptrend5m)
                {
                    condition.TrendDirection = TrendDirection.Up;
                }
                else if (downtrend1h && downtrend5m)
                {
                    condition.TrendDirection = TrendDirection.Down;
                }
                else
                {
                    condition.TrendDirection = TrendDirection.Sideways;
                }

                // Adjust strategy parameters based on market conditions
                AdjustStrategyParameters(condition, volatility5m, atrPercent, closes5m.Last());

                // Determine if market conditions are favorable for this strategy
                condition.IsFavorable = true;

                // Check for potential unfavorable conditions - strengthened criteria
                if (condition.TrendDirection == TrendDirection.Down && downtrend1h)
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Strong downtrend detected - not ideal for scalping";
                }

                if (!increasingVolume && condition.TrendDirection == TrendDirection.Down)
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Low volume in downtrend market";
                }

                if (condition.RSIVolatility < 35 && condition.TrendDirection == TrendDirection.Down)  // Increased from 30 to 35
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Oversold in downtrend - potential for further decline";
                }

                if (condition.RSIVolatility > 65 && !condition.MacdBullish)  // Reduced from 70 to 65
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Overbought with bearish MACD - potential reversal";
                }

                // Make sure we have enough volatility for good opportunities
                if (volatility5m < 0.001m) // Increased from 0.0005 to 0.001
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Extremely low volatility - not suitable for scalping";
                }

                // Avoid excessive volatility without trend
                if (volatility5m > 0.008m && !condition.IsStrongTrend)  // Reduced from 0.01 to 0.008
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Excessive volatility without clear trend - choppy market";
                }

                // Require recent momentum for better entries, but exempt uptrends with good RSI
                if (!recentMomentum &&
                    !(condition.TrendDirection == TrendDirection.Up && condition.RSIVolatility > 40 && condition.RSIVolatility < 70) &&
                    !(condition.MacdBullish && condition.RSIVolatility < 60))
                {
                    condition.IsFavorable = false;
                    condition.Reason = "Insufficient recent momentum - waiting for better entry";
                }



                return condition;
            }
            catch (Exception ex)
            {
                condition.IsFavorable = false;
                condition.Reason = $"Error analyzing market: {ex.Message}";
                return condition;
            }
        }

        private static void AdjustStrategyParameters(
            MarketCondition condition,
            decimal volatility5m,
            decimal atrPercent,
            decimal currentPrice)
        {
            // Dynamic buy threshold based on volatility - decreased slightly
            condition.BuyThreshold = Math.Max(0.01m, Math.Min(0.025m, volatility5m * 5));  // Reduced to allow more entries


            // Dynamic take profit based on ATR and trend - increased minimum targets
            if (condition.TrendDirection == TrendDirection.Up && condition.IsStrongTrend)
            {
                // More conservative profit target in uptrend (slightly reduced)
                condition.TakeProfitPercent = Math.Max(0.25m, Math.Min(0.7m, atrPercent * 0.75m));
            }
            else if (condition.TrendDirection == TrendDirection.Down || !condition.IsStrongTrend)
            {
                // Even more conservative in downtrend
                condition.TakeProfitPercent = Math.Max(0.2m, Math.Min(0.35m, atrPercent * 0.45m));  // Reduced from 0.25/0.4/0.5 to 0.2/0.35/0.45
            }
            else
            {
                // Default for sideways markets
                condition.TakeProfitPercent = Math.Max(0.2m, Math.Min(0.45m, atrPercent * 0.55m));  // Reduced from 0.25/0.5/0.6 to 0.2/0.45/0.55
            }

            // Dynamic stop loss based on ATR and trend - tightened to reduce potential losses
            if (condition.IsVolatile || condition.TrendDirection == TrendDirection.Down)
            {
                // Tighter stop loss in volatile or downtrend markets
                condition.StopLossPercent = Math.Max(0.2m, Math.Min(0.5m, atrPercent * 1.2m));  // Reduced from 0.3/0.6/1.5 to 0.2/0.5/1.2
            }
            else
            {
                // Even tighter stop loss in stable or uptrend markets
                condition.StopLossPercent = Math.Max(0.15m, Math.Min(0.35m, atrPercent * 0.9m));  // Reduced from 0.2/0.4/1.0 to 0.15/0.35/0.9
            }

            // Ensure stop loss is at least 1.5x take profit to maintain risk:reward
            if (condition.StopLossPercent < condition.TakeProfitPercent * 1.5m)
            {
                condition.StopLossPercent = condition.TakeProfitPercent * 1.5m;
            }
        }
    }
}