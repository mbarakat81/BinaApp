using System;
using System.Collections.Generic;
using TradingBot.Models;

namespace TradingBot.Utilities
{
    public static class ParameterOptimizer
    {
        public static MarketCondition OptimizeParameters(
            MarketCondition baseCondition,
            string symbol,
            Dictionary<string, StrategyMetrics> strategyPerformance)
        {
            if (strategyPerformance.ContainsKey(symbol))
            {
                var metrics = strategyPerformance[symbol];

                // Only optimize if we have enough data
                if (metrics.TotalTrades >= 5)
                {
                    // Adjust take profit based on historical performance
                    if (metrics.AvgProfit > 0 && metrics.WinRate > 0.6m)
                    {
                        // If we're winning consistently, slightly increase take profit target
                        baseCondition.TakeProfitPercent = Math.Min(0.5m, baseCondition.TakeProfitPercent * 1.05m);
                    }
                    else if (metrics.AvgProfit < 0 || metrics.WinRate < 0.4m)
                    {
                        // If we're losing, decrease take profit to secure gains earlier
                        baseCondition.TakeProfitPercent = Math.Max(0.1m, baseCondition.TakeProfitPercent * 0.95m);
                    }

                    // Adjust stop loss based on historical performance
                    if (metrics.MaxLoss < -0.3m && metrics.WinRate < 0.5m)
                    {
                        // If we're experiencing big losses, tighten stop loss
                        baseCondition.StopLossPercent = Math.Max(0.2m, baseCondition.StopLossPercent * 0.9m);
                    }

                    // Adjust buy threshold based on win rate
                    if (metrics.WinRate < 0.4m)
                    {
                        // If win rate is low, be more selective with entries
                        baseCondition.BuyThreshold = Math.Min(0.03m, baseCondition.BuyThreshold * 1.1m);
                    }

                    Console.WriteLine($"Optimized parameters for {symbol} based on {metrics.TotalTrades} trades, " +
                                     $"Win rate: {metrics.WinRate:P2}, Avg. profit: ${metrics.AvgProfit:F4}");
                }
            }

            return baseCondition;
        }
    }
}
