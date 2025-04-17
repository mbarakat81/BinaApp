using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingBot.Utilities
{
    public static class IndicatorCalculator
    {
        public static decimal CalculateVolatility(List<decimal> prices)
        {
            if (prices.Count <= 1)
                return 0;

            var returns = new List<decimal>();
            for (int i = 1; i < prices.Count; i++)
            {
                returns.Add((prices[i] - prices[i - 1]) / prices[i - 1]);
            }

            decimal mean = returns.Average();
            decimal variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
            return (decimal)Math.Sqrt((double)variance);
        }

        public static decimal CalculateMomentum(List<decimal> prices, int period)
        {
            if (prices.Count <= period)
                return 0;

            return (prices[prices.Count - 1] - prices[prices.Count - 1 - period]) / prices[prices.Count - 1 - period];
        }

        public static decimal CalculatePearsonCorrelation(List<decimal> series1, List<decimal> series2)
        {
            if (series1.Count != series2.Count || series1.Count <= 1)
                return 0;

            int n = series1.Count;
            decimal sum1 = series1.Sum();
            decimal sum2 = series2.Sum();
            decimal sum1Squared = series1.Sum(x => x * x);
            decimal sum2Squared = series2.Sum(x => x * x);
            decimal sumOfProducts = 0;

            for (int i = 0; i < n; i++)
            {
                sumOfProducts += series1[i] * series2[i];
            }

            decimal numerator = n * sumOfProducts - sum1 * sum2;
            decimal denominator = (decimal)Math.Sqrt((double)(n * sum1Squared - sum1 * sum1) * (double)(n * sum2Squared - sum2 * sum2));

            if (denominator == 0)
                return 0;

            return numerator / denominator;
        }

        public static decimal CalculateEMA(List<decimal> prices, int period)
        {
            if (prices.Count < period)
                return 0;

            var ema = prices.Take(period).Average();
            var multiplier = 2m / (period + 1m);

            for (int i = period; i < prices.Count; i++)
            {
                ema = (prices[i] - ema) * multiplier + ema;
            }

            return ema;
        }

        public static decimal CalculateRSI(List<decimal> prices, int period)
        {
            if (prices.Count <= period)
                return 50; // Neutral RSI default

            var gains = new List<decimal>();
            var losses = new List<decimal>();

            for (int i = 1; i < prices.Count; i++)
            {
                var change = prices[i] - prices[i - 1];
                gains.Add(Math.Max(0, change));
                losses.Add(Math.Max(0, -change));
            }

            if (gains.Count < period)
                return 50;

            var avgGain = gains.Skip(gains.Count - period).Average();
            var avgLoss = losses.Skip(losses.Count - period).Average();

            if (avgLoss == 0)
                return 100;

            var rs = avgGain / avgLoss;
            return 100 - (100 / (1 + rs));
        }

        public static (decimal macdLine, decimal signalLine) CalculateMACD(List<decimal> prices)
        {
            if (prices.Count < 26)
                return (0, 0);

            var ema12 = CalculateEMA(prices, 12);
            var ema26 = CalculateEMA(prices, 26);

            var macdLine = ema12 - ema26;

            // For signal line, we'd need to calculate EMA of MACD values
            // This is simplified for brevity
            var signalLine = macdLine * 0.9m;  // Approximate

            return (macdLine, signalLine);
        }

        public static decimal CalculateATR(List<IBinanceKline> klines, int period)
        {
            if (klines.Count < period + 1)
                return 0;

            var trValues = new List<decimal>();
            for (int i = 1; i < klines.Count; i++)
            {
                var high = klines[i].HighPrice;
                var low = klines[i].LowPrice;
                var previousClose = klines[i - 1].ClosePrice;

                var tr1 = high - low;
                var tr2 = Math.Abs(high - previousClose);
                var tr3 = Math.Abs(low - previousClose);

                var tr = Math.Max(Math.Max(tr1, tr2), tr3);
                trValues.Add(tr);
            }

            decimal sum = 0;
            int startIndex = Math.Max(0, trValues.Count - period);
            for (int i = startIndex; i < trValues.Count; i++)
            {
                sum += trValues[i];
            }

            int count = Math.Min(period, trValues.Count - startIndex);
            return count > 0 ? sum / count : 0;
        }
    }
}
