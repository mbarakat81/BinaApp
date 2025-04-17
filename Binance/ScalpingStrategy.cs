using Binance.Net.Clients;
using Binance.Net.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Analyzers;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Objects;
using System.Text;
using System.Collections.Concurrent;
using Binance.Net.Interfaces;

namespace TradingBot.Strategies
{
    public static class ScalpingStrategy
    {
        // Dictionary to track low volatility coins and when to check them again
        private static readonly ConcurrentDictionary<string, DateTime> _lowVolatilityCoins = new ConcurrentDictionary<string, DateTime>();

        // Dictionary to track historical price highs during uptrends for each symbol
        private static readonly ConcurrentDictionary<string, List<decimal>> _uptrendHighs = new ConcurrentDictionary<string, List<decimal>>();

        // Dictionary to store previous period lowest prices and resistance levels for each symbol
        private static readonly ConcurrentDictionary<string, (decimal LowestPrice, decimal Resistance, DateTime RecordTime)> _previousPeriodPrices =
            new ConcurrentDictionary<string, (decimal, decimal, DateTime)>();

        // Constant for extremely low volatility threshold
        private const decimal EXTREMELY_LOW_VOLATILITY_THRESHOLD = 0.2m; // 0.2% volatility is considered extremely low

        // Store the highest price during uptrend for a symbol
        public static void RecordUptrendHigh(string symbol, decimal price)
        {
            if (!_uptrendHighs.ContainsKey(symbol))
            {
                _uptrendHighs[symbol] = new List<decimal>();
            }

            _uptrendHighs[symbol].Add(price);

            // Keep only last 10 uptrend highs
            if (_uptrendHighs[symbol].Count > 10)
            {
                _uptrendHighs[symbol].RemoveAt(0);
            }
        }

        // Record downtrend period price range
        public static void RecordDowntrendPeriodPrices(string symbol, decimal lowestPrice, decimal resistance)
        {
            _previousPeriodPrices[symbol] = (lowestPrice, resistance, DateTime.Now);
        }

        // Check if a coin should be skipped due to low volatility
        public static bool ShouldSkipSymbol(string symbol)
        {
            if (_lowVolatilityCoins.TryGetValue(symbol, out DateTime nextCheckTime))
            {
                if (DateTime.Now < nextCheckTime)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Skipping {symbol} due to previously detected extremely low volatility. Will check again after {nextCheckTime:HH:mm:ss}");
                    return true;
                }
                else
                {
                    // Time has passed, remove from skip list
                    _lowVolatilityCoins.TryRemove(symbol, out _);
                }
            }
            return false;
        }

        // Set a coin to be skipped for 2 hours due to extremely low volatility
        private static void MarkSymbolLowVolatility(string symbol)
        {
            DateTime nextCheckTime = DateTime.Now.AddHours(2);
            _lowVolatilityCoins.AddOrUpdate(symbol, nextCheckTime, (key, oldValue) => nextCheckTime);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Marked {symbol} as extremely low volatility. Will skip until {nextCheckTime:HH:mm:ss}");
        }


        // Get previous downtrend period prices
        private static (decimal LowestPrice, decimal Resistance, bool IsRecent) GetPreviousDowntrendPrices(string symbol)
        {
            if (_previousPeriodPrices.TryGetValue(symbol, out var data))
            {
                // Check if the data is recent (less than 24 hours old)
                bool isRecent = (DateTime.Now - data.RecordTime).TotalHours < 24;
                return (data.LowestPrice, data.Resistance, isRecent);
            }
            return (0, 0, false);
        }

        public static async Task<TradeResult> ExecuteEnhancedScalpingStrategy(
            BinanceRestClient client,
            string symbol,
            decimal budget,
            decimal stepSize,
            decimal minQuantity,
            decimal tickSize,
            decimal takerFee,
            decimal makerFee,
            MarketCondition marketCondition,
            List<KeyPriceLevel> priceLevels,
            OrderBookSignal orderBookSignal)
        {
            var result = new TradeResult();
            result.Symbol = symbol;

            // Check if symbol should be skipped due to previously detected extremely low volatility
            if (ShouldSkipSymbol(symbol))
            {
                result.ErrorMessage = "Symbol temporarily skipped due to extremely low volatility";
                return result;
            }

            // Check for extremely low volatility - if found, mark to skip for 2 hours
            if (marketCondition.RSI < EXTREMELY_LOW_VOLATILITY_THRESHOLD)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Detected extremely low volatility for {symbol}: {marketCondition.RSI:F3}%");
                MarkSymbolLowVolatility(symbol);
                result.ErrorMessage = "Extremely low volatility detected, skipping symbol for 2 hours";
                return result;
            }

            // Initialize trading parameters from market conditions
            decimal stopLossPercent = Math.Max(0.5m, marketCondition.StopLossPercent); // Minimum 0.5% stop loss
            decimal takeProfitPercent = Math.Max(0.4m, marketCondition.TakeProfitPercent);

            // Make sure stop loss is at least 0.5% to avoid immediate triggers
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Strategy execution started for {symbol}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Stop loss set at {stopLossPercent:F2}%, Take profit at {takeProfitPercent:F2}%");

            // Dynamic adjustment based on market conditions
            if (marketCondition.RSI < 0.3m)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Low volatility detected ({marketCondition.RSI:F2}%), adjusting parameters");
                takeProfitPercent = Math.Max(0.3m, takeProfitPercent * 0.8m);
            }

            // Check support/resistance levels
            bool nearStrongSupport = priceLevels
                .Where(p => p.Type == "Support" && p.Strength >= 3)
                .Any();

            if (nearStrongSupport)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Price near strong support, increasing target profit");
                takeProfitPercent *= 1.2m; // Increase take profit when near support
            }

            // Get current price
            var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
            if (!ticker.Success)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to get current price: {ticker.Error?.Message}");
                result.ErrorMessage = ticker.Error?.Message;
                return result;
            }

            decimal currentPrice = ticker.Data.LastPrice;

            // Calculate quantity based on budget and current price
            decimal quantity = CalculateQuantity(budget, currentPrice, stepSize, minQuantity);

            // Ensure minimum quantity is met
            if (quantity < minQuantity)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Calculated quantity ({quantity}) is less than minimum ({minQuantity}). Adjusting to minimum.");
                quantity = minQuantity;
            }

            // Check if calculated order value meets exchange minimums
            if (quantity * currentPrice < 10)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Order value ({quantity * currentPrice:F2} USDT) is too small. Exchange minimum is typically 10 USDT.");
                result.ErrorMessage = "Order value too small";
                return result;
            }

            // Log quantity to be purchased
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Planning to buy {quantity} {symbol} at ~{currentPrice}");

            // Execute entry with specific criteria
            bool inPosition = false;
            decimal buyPrice = 0;
            decimal highestPrice = 0;
            decimal lowestPrice = decimal.MaxValue;
            int trailingStopCheckCount = 0;
            decimal partialSellExecuted = 0;
            bool reducedPosition = false;
            decimal totalBuyValue = 0;
            decimal totalSellValue = 0;

            // Entry decision logic
            bool shouldEnter = ShouldEnterPosition(marketCondition, orderBookSignal, priceLevels);

            if (!shouldEnter)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entry criteria not met, skipping trade");
                result.ErrorMessage = "Entry criteria not met";
                return result;
            }

            // ----- BEGIN DIRECTIONAL ENTRY LOGIC -----

            // Analyze market direction and timeframes
            var analysisResult = await AnalyzeMarketDirection(client, symbol, priceLevels);
            TimeSpan activeTimeframe = analysisResult.timeframe;
            decimal idealEntryPrice = analysisResult.idealEntryPrice;
            var trendAnalysis = analysisResult.trend;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Market analysis complete - Direction: {trendAnalysis.Direction}, Timeframe: {activeTimeframe.TotalMinutes}min");

            // Get historical uptrend high for use in profit target calculation
            decimal historicalUptrendHigh = GetHistoricalUptrendHighPrice(symbol);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Historical uptrend high for {symbol}: {historicalUptrendHigh}");

            // If we have a previous uptrend high, adjust take profit target
            if (historicalUptrendHigh > 0 && historicalUptrendHigh > currentPrice)
            {
                decimal distanceToHigh = (historicalUptrendHigh - currentPrice) / currentPrice * 100;

                // If historical high is fairly close, use it to inform our take profit
                if (distanceToHigh < 10) // Within 10%
                {
                    decimal adjustedTakeProfit = Math.Min(distanceToHigh * 0.7m, takeProfitPercent * 1.5m);
                    if (adjustedTakeProfit > takeProfitPercent)
                    {
                        takeProfitPercent = adjustedTakeProfit;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Adjusted take profit to {takeProfitPercent:F2}% based on historical uptrend high");
                    }
                }
            }

            // If analysis couldn't determine a good entry price, use current price as reference
            if (idealEntryPrice == 0)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Could not determine ideal entry price, using current price as reference");
                idealEntryPrice = currentPrice * 0.999m; // Slightly below current
            }

            // Special handling for downtrends - check for previous downtrend period prices
            if (trendAnalysis.Direction == MarketDirection.Downtrend)
            {
                var previousDowntrendData = GetPreviousDowntrendPrices(symbol);

                if (previousDowntrendData.IsRecent && previousDowntrendData.LowestPrice > 0)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Using previous downtrend data - Lowest: {previousDowntrendData.LowestPrice}, Resistance: {previousDowntrendData.Resistance}");

                    // In downtrends, target entry between previous low and resistance
                    decimal previousLow = previousDowntrendData.LowestPrice;
                    decimal previousResistance = previousDowntrendData.Resistance;

                    // Calculate ideal entry at 30% of the range from low to resistance
                    decimal range = previousResistance - previousLow;
                    decimal targetEntryPoint = previousLow + (range * 0.3m);

                    // Only use this if it's below current price (we want better entries in downtrends)
                    if (targetEntryPoint < currentPrice * 0.99m)
                    {
                        idealEntryPrice = targetEntryPoint;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Adjusted ideal entry to {idealEntryPrice} based on previous downtrend range");
                    }
                }

                // In downtrends, check if current price is near period closing price
                bool isPeriodEnd = IsNearPeriodClosing();
                if (isPeriodEnd)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Near period closing in downtrend, being cautious with entry");

                    // If using market order, reconsider to limit order with more conservative price
                    if (currentPrice > idealEntryPrice * 1.01m)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Current price too close to period closing price in downtrend, adjusting entry strategy");
                        idealEntryPrice = Math.Min(idealEntryPrice, currentPrice * 0.99m);
                    }
                }
            }

            // Configure entry strategy based on market direction
            decimal buyLimitPrice = idealEntryPrice;
            bool useMarketOrder = false;
            TimeSpan maxWaitTime = TimeSpan.FromMinutes(7);

            switch (trendAnalysis.Direction)
            {
                case MarketDirection.Uptrend:
                    // In uptrends, prioritize rapid entry
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Uptrend strategy - prioritizing quick entry");

                    // Record current price as potential uptrend high
                    RecordUptrendHigh(symbol, currentPrice);

                    if (trendAnalysis.Strength >= 3)
                    {
                        // Strong uptrend - use market order for immediate entry
                        useMarketOrder = true;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Strong uptrend detected - using market order to catch momentum");
                        maxWaitTime = TimeSpan.FromMinutes(1); // Set a default value
                    }
                    else
                    {
                        // Moderate uptrend - use limit order slightly above current price for quick fill
                        buyLimitPrice = currentPrice * 1.001m;
                        maxWaitTime = TimeSpan.FromMinutes(3); // Short wait time in uptrend
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Moderate uptrend - using limit order at {buyLimitPrice} with {maxWaitTime.TotalMinutes}min max wait");
                    }
                    break;

                case MarketDirection.Downtrend:
                    // In downtrends, be more patient for better entry
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Downtrend strategy - targeting lower entry price");

                    if (trendAnalysis.PotentialReversal)
                    {
                        // Potential reversal detected - position for bounce
                        buyLimitPrice = currentPrice * 0.998m;
                        maxWaitTime = TimeSpan.FromMinutes(5); // Medium wait time for reversal
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Potential reversal in downtrend - targeting entry at {buyLimitPrice}");
                    }
                    else
                    {
                        // Ongoing downtrend - target our calculated ideal price
                        buyLimitPrice = Math.Min(idealEntryPrice, currentPrice * 0.995m);
                        maxWaitTime = TimeSpan.FromMinutes(10); // Longer wait time in clear downtrend
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ongoing downtrend - patience mode activated, targeting {buyLimitPrice}");

                        // Check if we captured price data for this downtrend yet
                        var klineData = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneHour, limit: 12);
                        if (klineData.Success)
                        {
                            decimal lowestPriceInPeriod = klineData.Data.Min(k => k.LowPrice);
                            decimal highestPriceInPeriod = klineData.Data.Max(k => k.HighPrice);

                            // Store this data for future reference
                            RecordDowntrendPeriodPrices(symbol, lowestPriceInPeriod, highestPriceInPeriod);

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Recorded downtrend period data - Low: {lowestPriceInPeriod}, Resistance: {highestPriceInPeriod}");
                        }
                    }
                    break;

                default: // Sideways market
                    // In sideways market, be opportunistic
                    buyLimitPrice = idealEntryPrice;
                    maxWaitTime = TimeSpan.FromMinutes(7);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sideways market - targeting entry at {buyLimitPrice}");
                    break;
            }

            // Round limit price to tick size if using limit order
            if (!useMarketOrder)
            {
                buyLimitPrice = Math.Floor(buyLimitPrice / tickSize) * tickSize;
            }

            // If uptrend is strong enough, just use market order immediately
            if (useMarketOrder)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Executing market buy to catch uptrend");
                var marketOrderResult = await PlaceOrder(client, symbol, OrderSide.Buy, quantity);

                if (marketOrderResult.Success)
                {
                    inPosition = true;
                    buyPrice = currentPrice;
                    highestPrice = buyPrice;
                    lowestPrice = buyPrice;
                    totalBuyValue = quantity * buyPrice;
                    trailingStopCheckCount = 0;

                    // Update result
                    result.EntryPrice = buyPrice;
                    result.EntryTime = DateTime.Now;
                    result.Quantity = quantity;

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Market buy executed to catch uptrend. Price: {buyPrice}");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Market buy failed: {marketOrderResult.Error?.Message}");
                    useMarketOrder = false; // Fall back to limit order approach

                    // Initialize buyLimitPrice for the fallback scenario
                    buyLimitPrice = currentPrice * 1.001m;
                }
            }

            // If not using market order or market order failed, proceed with limit order logic
            if (!useMarketOrder)
            {
                // Track entry time and order attempts
                DateTime entryStartTime = DateTime.Now;
                long? currentOrderId = null;
                DateTime lastOrderUpdate = DateTime.Now;
                decimal lowestSeenPrice = currentPrice;
                int buyAttempts = 0;
                bool priceUpdated = false;
                DateTime lastReversalCheck = DateTime.Now;
                bool reversalDetected = false;

                // Create a loop to track price movements and optimize entry
                while (!inPosition && (DateTime.Now - entryStartTime) < maxWaitTime)
                {
                    try
                    {
                        buyAttempts++;

                        // Get current price
                        var refreshedTicker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
                        if (!refreshedTicker.Success)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to get current price: {refreshedTicker.Error?.Message}");
                            await Task.Delay(1000);
                            continue;
                        }

                        decimal previousPrice = currentPrice;
                        currentPrice = refreshedTicker.Data.LastPrice;

                        // Update lowest seen price if price drops
                        if (currentPrice < lowestSeenPrice)
                        {
                            lowestSeenPrice = currentPrice;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] New lowest price: {lowestSeenPrice}");
                            priceUpdated = true;
                        }

                        // Check for reversal signals (every 30 seconds)
                        if ((DateTime.Now - lastReversalCheck).TotalSeconds > 30 && trendAnalysis.Direction == MarketDirection.Downtrend)
                        {
                            lastReversalCheck = DateTime.Now;

                            // Simple reversal detection: price rising after significant drop
                            decimal priceChangePercent = (currentPrice - lowestSeenPrice) / lowestSeenPrice * 100;

                            if (priceChangePercent > 0.3m) // Price bounced at least 0.3% from lowest
                            {
                                reversalDetected = true;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Potential price reversal detected: {priceChangePercent:F2}% up from recent low");

                                // In downtrend with reversal, trigger quicker entry
                                buyLimitPrice = currentPrice * 1.001m; // Slightly above current to ensure fill

                                // Cancel any existing order to replace with more aggressive one
                                if (currentOrderId != null)
                                {
                                    await client.SpotApi.Trading.CancelOrderAsync(symbol, currentOrderId.Value);
                                    currentOrderId = null;
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cancelled existing order to place more aggressive order after reversal");
                                }
                            }
                        }

                        // Dynamic order adjustment based on price movement and time
                        if (currentOrderId == null ||
                           (priceUpdated && (DateTime.Now - lastOrderUpdate).TotalSeconds > 15))
                        {
                            // If we need to replace an existing order, cancel it first
                            if (currentOrderId != null)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Canceling existing order");
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, currentOrderId.Value);
                                currentOrderId = null;
                            }

                            // Calculate optimal entry price based on current conditions
                            decimal entryAdjustedPrice; // Fixed variable name to avoid conflict

                            // Adjust strategy based on time elapsed and market direction
                            TimeSpan elapsedTime = DateTime.Now - entryStartTime;
                            double timeRatio = elapsedTime.TotalMilliseconds / maxWaitTime.TotalMilliseconds;

                            if (trendAnalysis.Direction == MarketDirection.Downtrend && !reversalDetected)
                            {
                                // Special check for period closing in downtrend
                                bool nearPeriodClosing = IsNearPeriodClosing();

                                if (nearPeriodClosing)
                                {
                                    // Be more conservative near period closing in downtrend
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Near period closing in downtrend - being cautious");
                                    entryAdjustedPrice = Math.Min(idealEntryPrice, currentPrice * 0.992m);
                                }
                                // In downtrend without reversal, be patient early but more aggressive later
                                else if (timeRatio < 0.5) // First half of wait time
                                {
                                    // More patient, target better price
                                    entryAdjustedPrice = Math.Min(idealEntryPrice, currentPrice * 0.997m);
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Early downtrend phase - patience mode, targeting {entryAdjustedPrice}");
                                }
                                else if (timeRatio < 0.8) // Later phase
                                {
                                    // Getting more aggressive
                                    entryAdjustedPrice = Math.Min(idealEntryPrice, currentPrice * 0.999m);
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Later downtrend phase - becoming more aggressive, targeting {entryAdjustedPrice}");
                                }
                                else // Final phase
                                {
                                    // Very aggressive, ensure entry
                                    entryAdjustedPrice = currentPrice * 1.001m; // Slightly above market to ensure fill
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Final phase - ensuring entry at {entryAdjustedPrice}");
                                }
                            }
                            else if (reversalDetected || trendAnalysis.Direction == MarketDirection.Uptrend)
                            {
                                // In uptrend or after reversal, prioritize quick entry
                                entryAdjustedPrice = currentPrice * 1.001m;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Uptrend/reversal detected - quick entry mode at {entryAdjustedPrice}");
                            }
                            else // Sideways
                            {
                                if (timeRatio < 0.6) // First part of wait time
                                {
                                    // Target original calculated price
                                    entryAdjustedPrice = idealEntryPrice;
                                }
                                else // Later, become more aggressive
                                {
                                    // Move closer to current price
                                    entryAdjustedPrice = (idealEntryPrice + currentPrice) / 2;
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sideways market, time passing - adjusting to {entryAdjustedPrice}");
                                }
                            }

                            // Apply tick size rounding
                            entryAdjustedPrice = Math.Floor(entryAdjustedPrice / tickSize) * tickSize;
                            buyLimitPrice = entryAdjustedPrice;

                            // Place the new limit order
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Placing limit buy at {buyLimitPrice}, quantity: {quantity}");
                            var limitOrderResult = await PlaceLimitOrder(client, symbol, OrderSide.Buy, quantity, buyLimitPrice);

                            if (limitOrderResult.Success)
                            {
                                currentOrderId = limitOrderResult.Data.Id;
                                lastOrderUpdate = DateTime.Now;
                                priceUpdated = false;

                                // Check if order filled immediately
                                if (limitOrderResult.Data.Status == OrderStatus.Filled)
                                {
                                    inPosition = true;
                                    buyPrice = limitOrderResult.Data.Price;
                                    highestPrice = buyPrice;
                                    lowestPrice = buyPrice;
                                    totalBuyValue = quantity * buyPrice;
                                    trailingStopCheckCount = 0;

                                    result.EntryPrice = buyPrice;
                                    result.EntryTime = DateTime.Now;
                                    result.Quantity = quantity;

                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit order filled immediately at {buyPrice}");
                                    break;
                                }
                            }
                            else
                            {
                                // If order placement failed, log error and maybe try market order
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to place limit order: {limitOrderResult.Error?.Message}");

                                // If we're running out of time or had multiple failures, try market order
                                if (timeRatio > 0.8 || buyAttempts > 3)
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit order failed, switching to market order");
                                    var marketOrderResult = await PlaceOrder(client, symbol, OrderSide.Buy, quantity);

                                    if (marketOrderResult.Success)
                                    {
                                        inPosition = true;
                                        buyPrice = currentPrice;
                                        highestPrice = buyPrice;
                                        lowestPrice = buyPrice;
                                        totalBuyValue = quantity * buyPrice;
                                        trailingStopCheckCount = 0;

                                        result.EntryPrice = buyPrice;
                                        result.EntryTime = DateTime.Now;
                                        result.Quantity = quantity;

                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Market order executed at {buyPrice}");
                                        break;
                                    }
                                }
                            }
                        }

                        // Check if existing order has been filled
                        if (currentOrderId != null)
                        {
                            var orderStatus = await client.SpotApi.Trading.GetOrderAsync(symbol, currentOrderId.Value);
                            if (orderStatus.Success && orderStatus.Data.Status == OrderStatus.Filled)
                            {
                                inPosition = true;
                                buyPrice = orderStatus.Data.Price;
                                highestPrice = buyPrice;
                                lowestPrice = buyPrice;
                                totalBuyValue = quantity * buyPrice;
                                trailingStopCheckCount = 0;

                                result.EntryPrice = buyPrice;
                                result.EntryTime = DateTime.Now;
                                result.Quantity = quantity;

                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit order filled at {buyPrice}");
                                break;
                            }
                        }

                        // If max wait time almost reached, ensure execution with market order
                        if ((maxWaitTime - (DateTime.Now - entryStartTime)).TotalSeconds < 10 && !inPosition)
                        {
                            // Cancel any existing order
                            if (currentOrderId != null)
                            {
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, currentOrderId.Value);
                                currentOrderId = null;
                            }

                            // Place final market order
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Max wait time reached, executing market order");
                            var finalMarketOrder = await PlaceOrder(client, symbol, OrderSide.Buy, quantity);

                            if (finalMarketOrder.Success)
                            {
                                inPosition = true;
                                buyPrice = currentPrice;
                                highestPrice = buyPrice;
                                lowestPrice = buyPrice;
                                totalBuyValue = quantity * buyPrice;
                                trailingStopCheckCount = 0;

                                result.EntryPrice = buyPrice;
                                result.EntryTime = DateTime.Now;
                                result.Quantity = quantity;

                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Final market order executed at {buyPrice}");
                                break;
                            }
                        }

                        // Short delay between price checks - more frequent in uptrends
                        await Task.Delay(trendAnalysis.Direction == MarketDirection.Uptrend ? 1000 : 2000);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error during entry logic: {ex.Message}");
                        await Task.Delay(2000);
                    }
                }

                // If we somehow exited the loop without opening a position, force a market entry
                if (!inPosition)
                {
                    // Cancel any existing order
                    if (currentOrderId != null)
                    {
                        try
                        {
                            await client.SpotApi.Trading.CancelOrderAsync(symbol, currentOrderId.Value);
                            currentOrderId = null;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling order: {ex.Message}");
                        }
                    }

                    // Final attempt with market order
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Final fallback to market order");
                    var emergencyMarketOrder = await PlaceOrder(client, symbol, OrderSide.Buy, quantity);

                    if (emergencyMarketOrder.Success)
                    {
                        inPosition = true;
                        buyPrice = currentPrice;
                        highestPrice = buyPrice;
                        lowestPrice = buyPrice;
                        totalBuyValue = quantity * buyPrice;
                        trailingStopCheckCount = 0;

                        result.EntryPrice = buyPrice;
                        result.EntryTime = DateTime.Now;
                        result.Quantity = quantity;

                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Emergency market order executed at {buyPrice}");
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to enter position: {emergencyMarketOrder.Error?.Message}");
                        result.ErrorMessage = "Failed to enter position after multiple attempts";
                        return result;
                    }
                }
            }

            // ----- END DIRECTIONAL ENTRY LOGIC -----

            // If in uptrend, record the buy price as a potential uptrend high for future reference
            if (trendAnalysis.Direction == MarketDirection.Uptrend)
            {
                RecordUptrendHigh(symbol, buyPrice);
            }

            // Verify that we actually have the position
            decimal checkActualBalance = await GetActualBalance(client, symbol);
            if (checkActualBalance < quantity * 0.9m)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WARNING: Actual balance ({checkActualBalance}) is significantly less than expected ({quantity})");

                // Update quantity to actual value if it's still worth trading
                if (checkActualBalance >= minQuantity)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Adjusting position tracking to match actual balance");
                    quantity = checkActualBalance;
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Actual balance too low to trade, aborting");
                    result.ErrorMessage = "Actual balance too low to trade";
                    return result;
                }
            }

            // Position management loop
            DateTime positionEntryTime = DateTime.Now;
            DateTime lastPriceCheck = DateTime.Now;

            // Add trailing take profit variables here
            bool trailingTakeProfitActive = false;
            decimal trailingStopPrice = 0;
            decimal trailingReferencePrice = 0;
            decimal newTakeProfitPrice = 0;

            // Variables for progressive trailing stop adjustment
            int profitTargetHitCount = 0;
            decimal initialBuyPrice = buyPrice; // Store original buy price
            decimal adjustedBuyPrice = buyPrice; // This will be updated as we secure profits

            while (inPosition)
            {
                try
                {
                    // Get current price
                    var latestTicker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
                    if (!latestTicker.Success)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to get current price: {latestTicker.Error?.Message}");
                        await Task.Delay(1000);
                        continue;
                    }

                    currentPrice = latestTicker.Data.LastPrice;
                    lastPriceCheck = DateTime.Now;

                    // Update highest and lowest price
                    highestPrice = Math.Max(highestPrice, currentPrice);
                    lowestPrice = Math.Min(lowestPrice, currentPrice);

                    // Calculate current profit/loss in percentage terms
                    decimal currentProfitPercent = (currentPrice - buyPrice) / buyPrice * 100;

                    // Position time management
                    TimeSpan timeInPosition = DateTime.Now - positionEntryTime;

                    // ENHANCED TRAILING TAKE PROFIT LOGIC
                    if (currentProfitPercent > 0 && currentProfitPercent >= takeProfitPercent)
                    {
                        // Check if we're near resistance (skip trailing strategy if near resistance)
                        bool nearResistance = priceLevels
                            .Where(p => p.Type == "Resistance" && p.Strength >= 3 &&
                                      Math.Abs((p.Price - currentPrice) / currentPrice * 100) < 1.0m)
                            .Any();

                        if (nearResistance)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit hit at {currentPrice.ToString("0.00000000")} (Profit: {currentProfitPercent:F2}%), but near strong resistance - executing normal exit");

                            // Execute regular take profit
                            decimal remainingQty = quantity - partialSellExecuted;
                            decimal verifiedBalance = await GetActualBalance(client, symbol);
                            remainingQty = Math.Min(remainingQty, verifiedBalance);

                            if (remainingQty >= minQuantity)
                            {
                                // Ensure we respect step size rules
                                remainingQty = Math.Floor(remainingQty / stepSize) * stepSize;

                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit triggered at {currentPrice.ToString("0.00000000")} (Profit: {currentProfitPercent:F2}%)");

                                // Try to use limit order for better fill in profit taking
                                decimal limitSellPrice = currentPrice * 0.999m; // Slightly below market
                                limitSellPrice = Math.Round(limitSellPrice / tickSize) * tickSize;

                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Placing limit sell at {limitSellPrice.ToString("0.00000000")}");

                                var limitSellResult = await PlaceLimitOrder(client, symbol, OrderSide.Sell, remainingQty, limitSellPrice);

                                if (limitSellResult.Success)
                                {
                                    // Wait for order fill with 5 second timeout
                                    bool filled = await WaitForOrderFill(client, symbol, limitSellResult.Data.Id, TimeSpan.FromSeconds(5));

                                    if (!filled)
                                    {
                                        // Cancel limit order
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit sell order not filled, cancelling");
                                        await client.SpotApi.Trading.CancelOrderAsync(symbol, limitSellResult.Data.Id);
                                    }
                                    else
                                    {
                                        // Get actual filled price
                                        var orderStatus = await client.SpotApi.Trading.GetOrderAsync(symbol, limitSellResult.Data.Id);
                                        if (orderStatus.Success && orderStatus.Data.Status == OrderStatus.Filled)
                                        {
                                            currentPrice = orderStatus.Data.Price;
                                        }
                                    }
                                }

                                // If limit order didn't fill completely or failed, use market order
                                if (!limitSellResult.Success || !await IsOrderComplete(client, symbol, limitSellResult.Data.Id))
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Placing Sell MARKET order for {symbol}, Quantity: {remainingQty.ToString("0.00000000")}");
                                    var sellOrderResult = await PlaceOrder(client, symbol, OrderSide.Sell, remainingQty);

                                    if (!sellOrderResult.Success)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell order failed: {sellOrderResult.Error?.Message}");

                                        if (sellOrderResult.Error?.Message?.Contains("insufficient balance") == true)
                                        {
                                            // Retry with actual available balance
                                            decimal actualAvail = await GetActualBalance(client, symbol);
                                            if (actualAvail >= minQuantity)
                                            {
                                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retrying with actual available balance: {actualAvail}");
                                                actualAvail = Math.Floor(actualAvail / stepSize) * stepSize;
                                                var retryResult = await PlaceOrder(client, symbol, OrderSide.Sell, actualAvail);

                                                if (!retryResult.Success)
                                                {
                                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retry also failed: {retryResult.Error?.Message}");
                                                    await Task.Delay(1000);
                                                    continue;
                                                }

                                                // Update tracking values with actual sell
                                                remainingQty = actualAvail;
                                            }
                                            else
                                            {
                                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Available balance too small to sell, exiting position");
                                                inPosition = false;
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            await Task.Delay(1000);
                                            continue;
                                        }
                                    }
                                }

                                // Successfully sold position
                                inPosition = false;
                                totalSellValue += remainingQty * currentPrice;

                                result.Success = true;
                                result.ExitPrice = currentPrice;
                                result.ExitTime = DateTime.Now;
                                result.ExitReason = "Take Profit (Near Resistance)";

                                // Calculate actual profit
                                decimal totalBuy = quantity * buyPrice;
                                decimal totalSell = totalSellValue;

                                // Include fees
                                decimal estimatedFees = (totalBuy * takerFee) + (totalSell * takerFee);
                                decimal grossProfit = totalSell - totalBuy;
                                decimal netProfit = grossProfit - estimatedFees;

                                result.Profit = netProfit;

                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit sell executed successfully.");
                                return result;
                            }
                            else
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Quantity too small to sell");
                                inPosition = false;
                                result.Success = false;
                                result.ExitReason = "Insufficient Quantity";
                                return result;
                            }
                        }
                        else
                        {
                            // We've hit the profit target and we're NOT near resistance
                            // Implement enhanced trailing take profit strategy with progressive stop loss

                            // Increment count of how many times we've hit profit target
                            profitTargetHitCount++;

                            // Convert to trailing take profit mode if not already activated
                            if (!trailingTakeProfitActive)
                            {
                                trailingTakeProfitActive = true;

                                // Set initial trailing stop % (tighter than original stop loss)
                                // Make it dynamic - higher profits mean we can use wider trailing stops
                                decimal trailingPercent = Math.Max(0.3m, Math.Min(1.0m, currentProfitPercent * 0.2m));

                                // NEW: Calculate profit amount and set new adjusted buy price
                                decimal profitAmount = currentPrice - buyPrice;
                                adjustedBuyPrice = buyPrice + (profitAmount / 2); // Move buy price up by half the profit

                                // Set the initial trailing stop level to ensure we don't go below adjusted buy price
                                decimal calculatedStopPrice = currentPrice * (1 - trailingPercent / 100);
                                trailingStopPrice = Math.Max(calculatedStopPrice, adjustedBuyPrice);

                                // Update the initial reference price
                                trailingReferencePrice = currentPrice;

                                // Set a new take profit target above current price
                                newTakeProfitPrice = currentPrice * (1 + (takeProfitPercent * 0.5m) / 100);

                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trailing take profit activated - Current: {currentPrice}, Adjusted buy price: {adjustedBuyPrice}, Trailing Stop: {trailingStopPrice} ({trailingPercent:F2}%), New Target: {newTakeProfitPrice}");
                            }
                            // If already in trailing take profit mode and we hit a new high
                            else if (currentPrice > trailingReferencePrice)
                            {
                                // Calculate how much the price has moved up
                                decimal moveUpPercent = (currentPrice - trailingReferencePrice) / trailingReferencePrice * 100;

                                // Only update if the move is significant (prevents updating on tiny moves)
                                if (moveUpPercent >= 0.2m)
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] New high in trailing take profit mode: {currentPrice} (+{moveUpPercent:F2}% from {trailingReferencePrice})");

                                    // If we've hit profit target multiple times, progressively raise the adjusted buy price
                                    if (profitTargetHitCount > 1)
                                    {
                                        // Calculate how much more profit we've made since last adjustment
                                        decimal additionalProfit = currentPrice - trailingReferencePrice;

                                        // Move adjusted buy price up by half of the additional profit
                                        decimal newAdjustedBuyPrice = adjustedBuyPrice + (additionalProfit / 2);

                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Raising adjusted buy price from {adjustedBuyPrice} to {newAdjustedBuyPrice}");
                                        adjustedBuyPrice = newAdjustedBuyPrice;
                                    }

                                    // Update trailing stop - moves up with price but preserves some distance
                                    // As we get higher profits, we can narrow the trailing stop
                                    decimal trailingPercent = Math.Max(0.3m, Math.Min(0.8m, currentProfitPercent * 0.15m));
                                    decimal calculatedStopPrice = currentPrice * (1 - trailingPercent / 100);

                                    // Ensure stop is never below our adjusted buy price
                                    decimal newStopPrice = Math.Max(calculatedStopPrice, adjustedBuyPrice);

                                    // Only raise the stop, never lower it
                                    if (newStopPrice > trailingStopPrice)
                                    {
                                        trailingStopPrice = newStopPrice;
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Raised trailing stop to: {trailingStopPrice} ({trailingPercent:F2}% below current price or at adjusted buy price)");
                                    }

                                    // Update the reference price
                                    trailingReferencePrice = currentPrice;

                                    // Update the new take profit target
                                    newTakeProfitPrice = currentPrice * (1 + (takeProfitPercent * 0.5m) / 100);
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] New profit target: {newTakeProfitPrice}");
                                }
                            }
                        }
                    }

                    // Check if trailing stop is triggered
                    if (trailingTakeProfitActive && currentPrice <= trailingStopPrice)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trailing stop triggered at {currentPrice.ToString("0.00000000")} (Trail stop: {trailingStopPrice})");

                        decimal remainingQty = quantity - partialSellExecuted;
                        decimal verifiedBalance = await GetActualBalance(client, symbol);
                        remainingQty = Math.Min(remainingQty, verifiedBalance);

                        if (remainingQty >= minQuantity)
                        {
                            // Ensure we respect step size rules
                            remainingQty = Math.Floor(remainingQty / stepSize) * stepSize;

                            // For trailing stops, use market orders to ensure execution
                            var sellOrderResult = await PlaceOrder(client, symbol, OrderSide.Sell, remainingQty);

                            if (!sellOrderResult.Success)
                            {
                                if (sellOrderResult.Error?.Message?.Contains("insufficient balance") == true)
                                {
                                    // Retry with actual available balance
                                    decimal actualAvail = await GetActualBalance(client, symbol);
                                    if (actualAvail >= minQuantity)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retrying with actual available balance: {actualAvail}");
                                        actualAvail = Math.Floor(actualAvail / stepSize) * stepSize;
                                        var retryResult = await PlaceOrder(client, symbol, OrderSide.Sell, actualAvail);

                                        if (!retryResult.Success)
                                        {
                                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retry also failed: {retryResult.Error?.Message}");
                                            await Task.Delay(1000);
                                            continue;
                                        }

                                        // Update tracking values with actual sell
                                        remainingQty = actualAvail;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Available balance too small to sell, exiting position");
                                        inPosition = false;
                                        break;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell order failed: {sellOrderResult.Error?.Message}");
                                    await Task.Delay(1000);
                                    continue;
                                }
                            }

                            // Successfully sold position
                            inPosition = false;
                            totalSellValue += remainingQty * currentPrice;

                            result.Success = true;
                            result.ExitPrice = currentPrice;
                            result.ExitTime = DateTime.Now;
                            result.ExitReason = "Trailing Take Profit";

                            // Calculate actual profit
                            decimal totalBuy = quantity * buyPrice;
                            decimal totalSell = totalSellValue;

                            // Include fees
                            decimal estimatedFees = (totalBuy * takerFee) + (totalSell * takerFee);
                            decimal grossProfit = totalSell - totalBuy;
                            decimal netProfit = grossProfit - estimatedFees;

                            result.Profit = netProfit;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trailing take profit exit executed successfully. Profit: {netProfit:F8} USDT");

                            // If this was a good profit trade, record the highest price as uptrend high
                            if (netProfit > 0)
                            {
                                RecordUptrendHigh(symbol, highestPrice);
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Recorded {highestPrice} as uptrend high for future reference");
                            }

                            return result;
                        }
                        else
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trailing stop triggered but quantity too small to sell");
                            inPosition = false;
                            result.Success = true;
                            result.ExitReason = "Quantity Too Small";
                            result.ExitTime = DateTime.Now;
                            return result;
                        }
                    }

                    // Regular stop loss check (always active as a safety measure)
                    // If trailing take profit is active, use adjusted buy price for stop loss calculation
                    decimal effectiveBuyPrice = trailingTakeProfitActive ? adjustedBuyPrice : buyPrice;
                    decimal stopLossTriggerPrice = effectiveBuyPrice * (1 - stopLossPercent / 100);

                    if (currentPrice <= stopLossTriggerPrice)
                    {
                        decimal remainingQty = quantity - partialSellExecuted;

                        // Verify actual balance before selling
                        decimal verifBalance = await GetActualBalance(client, symbol);

                        // Always use the lesser of what we think we have and what we actually have
                        remainingQty = Math.Min(remainingQty, verifBalance);

                        if (remainingQty >= minQuantity)
                        {
                            decimal actualLossPercent = ((currentPrice - buyPrice) / buyPrice) * 100;

                            // Check if this is a loss relative to original entry or just relative to adjusted buy price
                            string exitReason = currentPrice > initialBuyPrice ?
                                "Protected Profit (Trailing Stop)" : "Stop Loss";

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {exitReason} triggered at {currentPrice.ToString("0.00000000")}, " +
                                            $"Original entry: {initialBuyPrice}, Adjusted entry: {effectiveBuyPrice}");

                            // Round down to ensure we don't exceed available balance
                            remainingQty = Math.Floor(remainingQty / stepSize) * stepSize;

                            // For stop losses, always use market orders to ensure execution
                            var sellOrderRes = await PlaceOrder(client, symbol, OrderSide.Sell, remainingQty);

                            if (!sellOrderRes.Success)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell order failed: {sellOrderRes.Error?.Message}");

                                if (sellOrderRes.Error?.Message?.Contains("insufficient balance") == true)
                                {
                                    // Retry with actual available balance
                                    decimal actualAvailable = await GetActualBalance(client, symbol);
                                    if (actualAvailable >= minQuantity)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retrying with actual available balance: {actualAvailable}");
                                        actualAvailable = Math.Floor(actualAvailable / stepSize) * stepSize;
                                        var retryResult = await PlaceOrder(client, symbol, OrderSide.Sell, actualAvailable);

                                        if (!retryResult.Success)
                                        {
                                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retry also failed: {retryResult.Error?.Message}");
                                            await Task.Delay(1000);
                                            continue;
                                        }

                                        // Update tracking values with actual sell
                                        remainingQty = actualAvailable;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Available balance too small to sell, exiting position");
                                        inPosition = false;
                                    }
                                }
                                else
                                {
                                    await Task.Delay(1000);
                                    continue;
                                }
                            }

                            inPosition = false;
                            result.Success = true;
                            result.ExitPrice = currentPrice;
                            result.ExitTime = DateTime.Now;
                            result.ExitReason = exitReason;

                            // Calculate profit/loss
                            decimal totalProfit = 0;

                            // Include any partial profits
                            if (partialSellExecuted > 0)
                            {
                                totalProfit += partialSellExecuted * (highestPrice * 0.997m - buyPrice);
                            }

                            // Final stop loss portion
                            totalProfit += remainingQty * (currentPrice - buyPrice);

                            // Subtract fees with small buffer
                            decimal feeBuffer = 0.0001m; // 0.01% extra buffer
                            decimal estimatedFees = (quantity * buyPrice * (takerFee + feeBuffer)) +
                                                 (remainingQty * currentPrice * (takerFee + feeBuffer));
                            if (partialSellExecuted > 0)
                            {
                                estimatedFees += partialSellExecuted * highestPrice * (takerFee + feeBuffer);
                            }

                            totalProfit -= estimatedFees;
                            result.Profit = totalProfit;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell order executed successfully.");

                            return result;
                        }
                        else
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Stop loss triggered but remaining quantity {remainingQty} is below minimum {minQuantity}");
                            // Forced exit since we can't sell
                            inPosition = false;
                            result.Success = true;
                            result.ExitReason = "Quantity too small to sell";
                            result.ExitTime = DateTime.Now;
                            result.ExitPrice = currentPrice;

                            // Calculate profit based on what we did sell
                            if (partialSellExecuted > 0)
                            {
                                result.Profit = (highestPrice * 0.998m - buyPrice) * partialSellExecuted;
                            }
                            else
                            {
                                result.Profit = 0;
                            }

                            return result;
                        }
                    }

                    // Time-based exit - if in profit and time in position is long
                    if (timeInPosition.TotalMinutes > 15 && currentProfitPercent > 0.1m)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Time-based exit triggered after {timeInPosition.TotalMinutes:F1} minutes with {currentProfitPercent:F2}% profit");

                        decimal remainingQty = quantity - partialSellExecuted;

                        // Verify actual balance
                        decimal verifyBal = await GetActualBalance(client, symbol);
                        remainingQty = Math.Min(remainingQty, verifyBal);

                        if (remainingQty >= minQuantity)
                        {
                            remainingQty = Math.Floor(remainingQty / stepSize) * stepSize;
                            var timeExitResult = await PlaceOrder(client, symbol, OrderSide.Sell, remainingQty);

                            if (!timeExitResult.Success)
                            {
                                if (timeExitResult.Error?.Message?.Contains("insufficient balance") == true)
                                {
                                    // Retry with actual available balance
                                    decimal actAvail = await GetActualBalance(client, symbol);
                                    if (actAvail >= minQuantity)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retrying with actual available balance: {actAvail}");
                                        actAvail = Math.Floor(actAvail / stepSize) * stepSize;
                                        var retryResult = await PlaceOrder(client, symbol, OrderSide.Sell, actAvail);

                                        if (!retryResult.Success)
                                        {
                                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retry also failed: {retryResult.Error?.Message}");
                                            await Task.Delay(1000);
                                            continue;
                                        }

                                        // Update tracking values with actual sell
                                        remainingQty = actAvail;
                                        timeExitResult = retryResult;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Available balance too small to sell, exiting position");
                                        inPosition = false;
                                        break;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell order failed: {timeExitResult.Error?.Message}");
                                    await Task.Delay(1000);
                                    continue;
                                }
                            }

                            inPosition = false;
                            result.Success = true;
                            result.ExitPrice = currentPrice;
                            result.ExitTime = DateTime.Now;
                            result.ExitReason = "Time Based Exit";

                            // Calculate profit
                            decimal profit = remainingQty * (currentPrice - buyPrice);

                            // Subtract fees
                            decimal estimatedFees = (remainingQty * buyPrice * takerFee) + (remainingQty * currentPrice * takerFee);
                            profit -= estimatedFees;

                            result.Profit = profit;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell order executed successfully.");

                            return result;
                        }
                        else
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Time-based exit but quantity too small to sell");
                            inPosition = false;
                            result.Success = true;
                            result.ExitReason = "Quantity too small";
                            result.ExitTime = DateTime.Now;
                            return result;
                        }
                    }

                    // Maximum time in trade - cut losses
                    if (timeInPosition.TotalMinutes > 40)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Maximum time in trade reached ({timeInPosition.TotalMinutes:F1} min). Exiting position.");

                        decimal remainingQty = quantity - partialSellExecuted;
                        decimal verifyBalance = await GetActualBalance(client, symbol);
                        remainingQty = Math.Min(remainingQty, verifyBalance);

                        if (remainingQty >= minQuantity)
                        {
                            remainingQty = Math.Floor(remainingQty / stepSize) * stepSize;
                            var maxTimeResult = await PlaceOrder(client, symbol, OrderSide.Sell, remainingQty);

                            if (!maxTimeResult.Success)
                            {
                                if (maxTimeResult.Error?.Message?.Contains("insufficient balance") == true)
                                {
                                    // Retry with actual available balance
                                    decimal actualAvail = await GetActualBalance(client, symbol);
                                    if (actualAvail >= minQuantity)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retrying with actual available balance: {actualAvail}");
                                        actualAvail = Math.Floor(actualAvail / stepSize) * stepSize;
                                        var retryResult = await PlaceOrder(client, symbol, OrderSide.Sell, actualAvail);

                                        if (!retryResult.Success)
                                        {
                                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retry also failed: {retryResult.Error?.Message}");
                                            await Task.Delay(1000);
                                            continue;
                                        }

                                        // Update tracking values with actual sell
                                        remainingQty = actualAvail;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Available balance too small to sell, exiting position");
                                        inPosition = false;
                                        break;
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell order failed: {maxTimeResult.Error?.Message}");
                                    await Task.Delay(1000);
                                    continue;
                                }
                            }

                            inPosition = false;
                            result.Success = true;
                            result.ExitPrice = currentPrice;
                            result.ExitTime = DateTime.Now;
                            result.ExitReason = "Max Time In Trade";

                            // Calculate profit/loss
                            decimal profit = remainingQty * (currentPrice - buyPrice);

                            // Subtract fees
                            decimal estimatedFees = (remainingQty * buyPrice * takerFee) + (remainingQty * currentPrice * takerFee);
                            profit -= estimatedFees;

                            result.Profit = profit;

                            return result;
                        }
                        else
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Max time in trade reached but quantity too small to sell");
                            inPosition = false;
                            result.Success = true;
                            result.ExitReason = "Quantity too small";
                            result.ExitTime = DateTime.Now;
                            return result;
                        }
                    }

                    // Short delay to avoid hammering the API
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error during position management: {ex.Message}");
                }
            }

            // If we exit the loop without a proper result
            if (!result.Success)
            {
                result.Success = true;
                result.ExitReason = "Unknown/Error";
                if (result.ExitTime == default)
                {
                    result.ExitTime = DateTime.Now;
                }

                // Calculate approximate profit if possible
                if (partialSellExecuted > 0)
                {
                    result.Profit = (highestPrice * 0.998m - buyPrice) * partialSellExecuted;
                    result.Profit -= (partialSellExecuted * buyPrice * takerFee) + (partialSellExecuted * highestPrice * takerFee);
                }
            }

            return result;
        }

        // Get historical uptrend high price for a symbol
        //private static decimal GetHistoricalUptrendHighPrice(string symbol)
        //{
        //    // Return 0 if we don't have data for this symbol
        //    if (!_uptrendHighs.TryGetValue(symbol, out var highs) || highs.Count == 0)
        //    {
        //        return 0;
        //    }

        //    // Return the maximum high if it's greater than current price
        //    decimal max = highs.Max();
        //    return max;
        //}

        // Get historical uptrend highs for a symbol
        private static decimal GetHistoricalUptrendHighPrice(string symbol)
        {
            if (_uptrendHighs.TryGetValue(symbol, out var highs) && highs.Count > 0)
            {
                return highs.Max();
            }
            return 0;
        }
        // Check if we're near the closing time of a period
        private static bool IsNearPeriodClosing()
        {
            DateTime now = DateTime.UtcNow;

            // Check if near the end of an hour
            if (now.Minute >= 55)
            {
                return true;
            }

            // Check if near end of 15-minute period
            int minutes = now.Minute % 15;
            if (minutes >= 12)
            {
                return true;
            }

            // Check if near end of 4-hour period
            int currentHour = now.Hour % 4;
            if (currentHour == 3 && now.Minute >= 50)
            {
                return true;
            }

            return false;
        }

        // Direction-specific market analysis
        private static async Task<(TimeSpan timeframe, decimal idealEntryPrice, TrendAnalysis trend)> AnalyzeMarketDirection(
            BinanceRestClient client,
            string symbol,
            List<KeyPriceLevel> priceLevels)
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Analyzing market direction...");

                // Get candle data for different timeframes
                var fifteenMinData = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FifteenMinutes,
                    limit: 30);

                var hourlyData = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.OneHour,
                    limit: 24);

                var fourHourData = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    KlineInterval.FourHour,
                    limit: 10);

                if (!fifteenMinData.Success || !hourlyData.Success || !fourHourData.Success)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to get complete kline data for direction analysis");
                    return (TimeSpan.FromMinutes(15), 0, new TrendAnalysis { Direction = MarketDirection.Sideways }); // Default values
                }

                // Determine if we're near period transitions (end of current period)
                bool near4hPeriodEnd = IsNearPeriodEnd(KlineInterval.FourHour);
                bool near1hPeriodEnd = IsNearPeriodEnd(KlineInterval.OneHour);
                bool near15mPeriodEnd = IsNearPeriodEnd(KlineInterval.FifteenMinutes);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Period end proximity: 4h: {near4hPeriodEnd}, 1h: {near1hPeriodEnd}, 15m: {near15mPeriodEnd}");

                // Variables to store analysis results
                TimeSpan selectedTimeframe;
                decimal idealEntryPrice = 0;
                var trendAnalysis = new TrendAnalysis();

                // Order candles from newest to oldest
                var fourHourCandles = fourHourData.Data.OrderByDescending(k => k.OpenTime).ToList();
                var hourlyCandles = hourlyData.Data.OrderByDescending(k => k.OpenTime).ToList();
                var fifteenMinCandles = fifteenMinData.Data.OrderByDescending(k => k.OpenTime).ToList();

                // Analyze 4h trend first (strongest timeframe)
                trendAnalysis = Analyze4HourTrend(fourHourCandles, near4hPeriodEnd, symbol);

                // If 4h shows strong signals or we're near 4h period end, use it
                if (trendAnalysis.Strength >= 3 || near4hPeriodEnd)
                {
                    selectedTimeframe = TimeSpan.FromHours(4);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Using 4h timeframe - Direction: {trendAnalysis.Direction}, Strength: {trendAnalysis.Strength}");

                    // Set entry price based on direction
                    if (trendAnalysis.Direction == MarketDirection.Uptrend)
                    {
                        // In strong uptrend, buy faster at current price
                        idealEntryPrice = fifteenMinCandles[0].ClosePrice * 1.001m; // Slightly above current to ensure quick fill
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Strong 4h uptrend - targeting immediate entry at {idealEntryPrice}");
                    }
                    else if (trendAnalysis.Direction == MarketDirection.Downtrend)
                    {
                        // In downtrend, target lower support area
                        decimal lowestLow = fourHourCandles.Take(3).Min(k => k.LowPrice);
                        decimal recentLow = Math.Min(fifteenMinCandles[0].LowPrice, hourlyCandles[0].LowPrice);

                        // Find nearby support levels
                        var nearbySupport = priceLevels
                            .Where(p => p.Type == "Support" &&
                                   p.Price < recentLow &&
                                   p.Price > lowestLow * 0.995m)
                            .OrderByDescending(p => p.Price)
                            .FirstOrDefault();

                        if (nearbySupport != null)
                        {
                            idealEntryPrice = nearbySupport.Price * 1.002m; // Slightly above support
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 4h downtrend with support at {nearbySupport.Price} - targeting entry at {idealEntryPrice}");
                        }
                        else
                        {
                            // No clear support, use Fibonacci level from recent range
                            decimal range = fourHourCandles[0].HighPrice - lowestLow;
                            idealEntryPrice = lowestLow + (range * 0.236m); // 23.6% Fib level
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 4h downtrend - targeting Fibonacci entry at {idealEntryPrice}");
                        }

                        // Store this data for future reference
                        RecordDowntrendPeriodPrices(symbol, lowestLow, fourHourCandles[0].HighPrice);
                    }
                    else // Sideways
                    {
                        // In sideways market, look for reversal signals at support
                        decimal recentLow = fifteenMinCandles.Take(8).Min(k => k.LowPrice);
                        decimal currentPrice = fifteenMinCandles[0].ClosePrice;

                        if (near4hPeriodEnd)
                        {
                            // End of period often sees reversal - be ready to catch it
                            idealEntryPrice = currentPrice * 1.001m; // Quick entry
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] End of 4h period in sideways market - targeting quick entry at {idealEntryPrice}");
                        }
                        else
                        {
                            // Normal sideways - target dip
                            idealEntryPrice = (recentLow + currentPrice) / 2;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sideways 4h market - targeting mid-range entry at {idealEntryPrice}");
                        }
                    }
                }
                else // If 4h doesn't show strong signals, check 1h
                {
                    trendAnalysis = Analyze1HourTrend(hourlyCandles, near1hPeriodEnd, symbol);

                    if (trendAnalysis.Strength >= 2 || near1hPeriodEnd)
                    {
                        selectedTimeframe = TimeSpan.FromHours(1);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Using 1h timeframe - Direction: {trendAnalysis.Direction}, Strength: {trendAnalysis.Strength}");

                        if (trendAnalysis.Direction == MarketDirection.Uptrend)
                        {
                            // In 1h uptrend, buy at current price to catch momentum
                            idealEntryPrice = fifteenMinCandles[0].ClosePrice * 1.0005m;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 1h uptrend - targeting immediate entry at {idealEntryPrice}");
                        }
                        else if (trendAnalysis.Direction == MarketDirection.Downtrend)
                        {
                            // In 1h downtrend, look for potential reversal points
                            decimal hourRange = hourlyCandles[0].HighPrice - hourlyCandles[0].LowPrice;

                            if (near1hPeriodEnd && trendAnalysis.PotentialReversal)
                            {
                                // If near period end with reversal signs, be ready to catch bottom
                                idealEntryPrice = fifteenMinCandles[0].ClosePrice * 0.998m;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 1h downtrend near period end with reversal signs - targeting entry at {idealEntryPrice}");
                            }
                            else
                            {
                                // Normal downtrend - target deeper pullback
                                decimal lowestRecent = hourlyCandles.Take(4).Min(k => k.LowPrice);
                                idealEntryPrice = Math.Min(fifteenMinCandles[0].LowPrice * 0.997m, lowestRecent * 1.003m);
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 1h downtrend - targeting pullback entry at {idealEntryPrice}");
                            }

                            // Store this data for future reference
                            RecordDowntrendPeriodPrices(symbol, hourlyCandles.Take(4).Min(k => k.LowPrice), hourlyCandles.Take(4).Max(k => k.HighPrice));
                        }
                        else // Sideways
                        {
                            // In sideways 1h, look for short-term price action
                            decimal vwap = CalculateVWAP(fifteenMinCandles.Take(8).ToList());
                            idealEntryPrice = vwap * 0.998m;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sideways 1h market - targeting VWAP-based entry at {idealEntryPrice}");
                        }
                    }
                    else // If neither 4h nor 1h shows strong signals, use 15min
                    {
                        selectedTimeframe = TimeSpan.FromMinutes(15);
                        trendAnalysis = Analyze15MinTrend(fifteenMinCandles, near15mPeriodEnd, symbol);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Using 15min timeframe - Direction: {trendAnalysis.Direction}, Strength: {trendAnalysis.Strength}");

                        if (trendAnalysis.Direction == MarketDirection.Uptrend)
                        {
                            // In short-term uptrend, buy immediately
                            idealEntryPrice = fifteenMinCandles[0].ClosePrice; // Market price
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 15min uptrend - targeting immediate entry at market price");
                        }
                        else if (trendAnalysis.Direction == MarketDirection.Downtrend)
                        {
                            // In short-term downtrend, wait for smallest sign of reversal
                            if (near15mPeriodEnd || trendAnalysis.PotentialReversal)
                            {
                                idealEntryPrice = fifteenMinCandles[0].ClosePrice * 0.999m;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 15min downtrend with potential reversal - targeting entry at {idealEntryPrice}");
                            }
                            else
                            {
                                decimal lowestRecent = fifteenMinCandles.Take(6).Min(k => k.LowPrice);
                                idealEntryPrice = lowestRecent * 1.002m;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 15min downtrend - targeting recent low entry at {idealEntryPrice}");
                            }

                            // Store this data for future reference
                            RecordDowntrendPeriodPrices(symbol, fifteenMinCandles.Take(6).Min(k => k.LowPrice), fifteenMinCandles.Take(6).Max(k => k.HighPrice));
                        }
                        else // Sideways
                        {
                            // In very short-term sideways, look for immediate price moves
                            decimal currentPrice = fifteenMinCandles[0].ClosePrice;
                            idealEntryPrice = currentPrice * 0.999m;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sideways 15min market - targeting immediate entry slightly below current price");
                        }
                    }
                }

                return (selectedTimeframe, idealEntryPrice, trendAnalysis);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error analyzing market direction: {ex.Message}");
                return (TimeSpan.FromMinutes(15), 0, new TrendAnalysis { Direction = MarketDirection.Sideways }); // Default values
            }

            // Helper function to check if we're near the end of a period
            bool IsNearPeriodEnd(KlineInterval interval)
            {
                DateTime now = DateTime.UtcNow;

                if (interval == KlineInterval.FifteenMinutes)
                {
                    int minutes = now.Minute % 15;
                    return minutes >= 12; // Last 3 minutes of 15-min period
                }
                else if (interval == KlineInterval.OneHour)
                {
                    return now.Minute >= 55; // Last 5 minutes of hourly period
                }
                else if (interval == KlineInterval.FourHour)
                {
                    int currentHour = now.Hour % 4;
                    return currentHour == 3 && now.Minute >= 50; // Last 10 minutes of 4-hour period
                }

                return false;
            }

            // Analyze 4-hour trend
            TrendAnalysis Analyze4HourTrend(List<IBinanceKline> candles, bool nearPeriodEnd, string symbolName) // Fixed by adding symbolName parameter
            {
                var trend = new TrendAnalysis();

                if (candles.Count < 3)
                    return trend;

                // Calculate basic indicators
                int bullishCandles = 0;
                int bearishCandles = 0;

                for (int i = 0; i < 3; i++)
                {
                    if (candles[i].ClosePrice > candles[i].OpenPrice)
                        bullishCandles++;
                    else if (candles[i].ClosePrice < candles[i].OpenPrice)
                        bearishCandles++;
                }

                // Check for higher highs & higher lows (uptrend)
                bool higherHigh = candles[0].HighPrice > candles[1].HighPrice && candles[1].HighPrice > candles[2].HighPrice;
                bool higherLow = candles[0].LowPrice > candles[1].LowPrice && candles[1].LowPrice > candles[2].LowPrice;

                // Check for lower lows & lower highs (downtrend)
                bool lowerLow = candles[0].LowPrice < candles[1].LowPrice && candles[1].LowPrice < candles[2].LowPrice;
                bool lowerHigh = candles[0].HighPrice < candles[1].HighPrice && candles[1].HighPrice < candles[2].HighPrice;

                // Check volume trend
                bool increasingVolume = candles[0].QuoteVolume > candles[1].QuoteVolume;

                // Check proximity to closing price in downtrend
                bool nearClosingPrice = false;
                if (candles[0].ClosePrice < candles[0].OpenPrice) // Current candle is bearish
                {
                    decimal candleRange = Math.Abs(candles[0].HighPrice - candles[0].LowPrice);
                    decimal closeToLowDistance = Math.Abs(candles[0].ClosePrice - candles[0].LowPrice);
                    nearClosingPrice = closeToLowDistance / candleRange < 0.2m; // Close is in bottom 20% of candle
                }

                // Determine trend direction
                if ((bullishCandles >= 2 && higherLow) || (higherHigh && higherLow))
                {
                    trend.Direction = MarketDirection.Uptrend;
                    trend.Strength = bullishCandles + (higherHigh ? 1 : 0) + (higherLow ? 1 : 0) + (increasingVolume ? 1 : 0);

                    // Record uptrend high for future reference
                    RecordUptrendHigh(symbolName, candles[0].HighPrice);
                }
                else if ((bearishCandles >= 2 && lowerHigh) || (lowerLow && lowerHigh))
                {
                    trend.Direction = MarketDirection.Downtrend;
                    trend.Strength = bearishCandles + (lowerLow ? 1 : 0) + (lowerHigh ? 1 : 0) + (increasingVolume ? 1 : 0);

                    // Special flag for when price is near closing in downtrend
                    trend.NearClosingPrice = nearClosingPrice;

                    // Check for potential reversal at period end
                    if (nearPeriodEnd)
                    {
                        decimal lastCandleSize = Math.Abs(candles[0].ClosePrice - candles[0].OpenPrice);
                        decimal previousCandleSize = Math.Abs(candles[1].ClosePrice - candles[1].OpenPrice);

                        if (lastCandleSize < previousCandleSize * 0.7m) // Diminishing momentum
                        {
                            trend.PotentialReversal = true;
                        }

                        // Doji or hammer formation at the end of downtrend
                        if (lastCandleSize < Math.Abs(candles[0].HighPrice - candles[0].LowPrice) * 0.3m &&
                            candles[0].LowPrice < candles[1].LowPrice)
                        {
                            trend.PotentialReversal = true;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Potential 4h reversal pattern detected");
                        }
                    }
                }
                else
                {
                    trend.Direction = MarketDirection.Sideways;
                    trend.Strength = 1;
                }

                return trend;
            }

            // Analyze 1-hour trend (similar structure, but more sensitive to recent moves)
            TrendAnalysis Analyze1HourTrend(List<IBinanceKline> candles, bool nearPeriodEnd, string symbolName) // Fixed by adding symbolName parameter
            {
                var trend = new TrendAnalysis();

                if (candles.Count < 6)
                    return trend;

                // Calculate basic indicators
                int bullishCandles = 0;
                int bearishCandles = 0;

                for (int i = 0; i < 4; i++) // Check last 4 hourly candles
                {
                    if (candles[i].ClosePrice > candles[i].OpenPrice)
                        bullishCandles++;
                    else if (candles[i].ClosePrice < candles[i].OpenPrice)
                        bearishCandles++;
                }

                // Calculate moving averages
                decimal ma3 = candles.Take(3).Average(c => c.ClosePrice);
                decimal ma6 = candles.Take(6).Average(c => c.ClosePrice);

                // Check proximity to closing price in downtrend
                bool nearClosingPrice = false;
                if (candles[0].ClosePrice < candles[0].OpenPrice) // Current candle is bearish
                {
                    decimal candleRange = Math.Abs(candles[0].HighPrice - candles[0].LowPrice);
                    decimal closeToLowDistance = Math.Abs(candles[0].ClosePrice - candles[0].LowPrice);
                    nearClosingPrice = closeToLowDistance / candleRange < 0.2m; // Close is in bottom 20% of candle
                }

                // Determine trend direction
                if (bullishCandles >= 3 || (ma3 > ma6 && candles[0].ClosePrice > ma3))
                {
                    trend.Direction = MarketDirection.Uptrend;
                    trend.Strength = Math.Min(4, bullishCandles + (candles[0].ClosePrice > ma3 ? 1 : 0));

                    // Record uptrend high for future reference
                    RecordUptrendHigh(symbolName, candles[0].HighPrice);
                }
                else if (bearishCandles >= 3 || (ma3 < ma6 && candles[0].ClosePrice < ma3))
                {
                    trend.Direction = MarketDirection.Downtrend;
                    trend.Strength = Math.Min(4, bearishCandles + (candles[0].ClosePrice < ma3 ? 1 : 0));

                    // Special flag for when price is near closing in downtrend
                    trend.NearClosingPrice = nearClosingPrice;

                    // Check for potential reversal patterns
                    if (nearPeriodEnd)
                    {
                        // Check for bullish engulfing or hammer
                        bool bullishEngulfing = candles[0].OpenPrice < candles[1].ClosePrice &&
                                              candles[0].ClosePrice > candles[1].OpenPrice &&
                                              candles[0].ClosePrice > candles[0].OpenPrice;

                        bool hammer = candles[0].LowPrice < candles[1].LowPrice &&
                                    (candles[0].ClosePrice - candles[0].LowPrice) > (candles[0].HighPrice - candles[0].ClosePrice) * 2;

                        if (bullishEngulfing || hammer)
                        {
                            trend.PotentialReversal = true;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Potential 1h reversal pattern detected");
                        }
                    }
                }
                else
                {
                    trend.Direction = MarketDirection.Sideways;
                    trend.Strength = 1;
                }

                return trend;
            }

            // Analyze 15-minute trend (most sensitive to immediate price action)
            TrendAnalysis Analyze15MinTrend(List<IBinanceKline> candles, bool nearPeriodEnd, string symbolName) // Fixed by adding symbolName parameter
            {
                var trend = new TrendAnalysis();

                if (candles.Count < 8)
                    return trend;

                // For 15min, focus on very recent price action
                int bullishCandles = 0;
                int bearishCandles = 0;

                for (int i = 0; i < 4; i++) // Check last 4 15min candles (last hour)
                {
                    if (candles[i].ClosePrice > candles[i].OpenPrice)
                        bullishCandles++;
                    else if (candles[i].ClosePrice < candles[i].OpenPrice)
                        bearishCandles++;
                }

                // Check momentum using close price differences
                decimal recentMomentum = candles[0].ClosePrice - candles[2].ClosePrice;
                decimal olderMomentum = candles[3].ClosePrice - candles[5].ClosePrice;

                // Check proximity to closing price in downtrend
                bool nearClosingPrice = false;
                if (candles[0].ClosePrice < candles[0].OpenPrice) // Current candle is bearish
                {
                    decimal candleRange = Math.Abs(candles[0].HighPrice - candles[0].LowPrice);
                    decimal closeToLowDistance = Math.Abs(candles[0].ClosePrice - candles[0].LowPrice);
                    nearClosingPrice = closeToLowDistance / candleRange < 0.2m; // Close is in bottom 20% of candle
                }

                if (bullishCandles >= 3 || recentMomentum > 0)
                {
                    trend.Direction = MarketDirection.Uptrend;
                    trend.Strength = bullishCandles;

                    if (recentMomentum > olderMomentum && recentMomentum > 0)
                        trend.Strength += 1; // Accelerating upward momentum

                    // Record uptrend high for future reference
                    RecordUptrendHigh(symbolName, candles[0].HighPrice);
                }
                else if (bearishCandles >= 3 || recentMomentum < 0)
                {
                    trend.Direction = MarketDirection.Downtrend;
                    trend.Strength = bearishCandles;

                    // Special flag for when price is near closing in downtrend
                    trend.NearClosingPrice = nearClosingPrice;

                    if (recentMomentum < olderMomentum && recentMomentum < 0)
                        trend.Strength += 1; // Accelerating downward momentum

                    // Check for immediate reversal signs
                    if (nearPeriodEnd ||
                       (candles[0].ClosePrice > candles[0].OpenPrice &&
                        candles[1].ClosePrice < candles[1].OpenPrice &&
                        candles[2].ClosePrice < candles[2].OpenPrice))
                    {
                        trend.PotentialReversal = true;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Potential 15min reversal pattern detected");
                    }
                }
                else
                {
                    trend.Direction = MarketDirection.Sideways;
                    trend.Strength = 1;
                }

                return trend;
            }
        }

        // Helper method to calculate Volume-Weighted Average Price
        private static decimal CalculateVWAP(List<IBinanceKline> candles)
        {
            if (candles == null || candles.Count == 0)
                return 0;

            decimal sumPriceVolume = 0;
            decimal sumVolume = 0;

            foreach (var candle in candles)
            {
                decimal typicalPrice = (candle.HighPrice + candle.LowPrice + candle.ClosePrice) / 3;
                decimal priceVolume = typicalPrice * candle.QuoteVolume;
                sumPriceVolume += priceVolume;
                sumVolume += candle.QuoteVolume;
            }

            if (sumVolume == 0)
                return 0;

            return sumPriceVolume / sumVolume;
        }

        // Market direction enumeration
        private enum MarketDirection
        {
            Uptrend,
            Downtrend,
            Sideways
        }

        // Trend analysis class
        private class TrendAnalysis
        {
            public MarketDirection Direction { get; set; } = MarketDirection.Sideways;
            public int Strength { get; set; } = 0;
            public bool PotentialReversal { get; set; } = false;
            public bool NearClosingPrice { get; set; } = false; // Added to track closing price proximity in downtrends
        }

        private static bool ShouldEnterPosition(MarketCondition marketCondition, OrderBookSignal orderBookSignal, List<KeyPriceLevel> priceLevels)
        {
            // Basic entry requirement
            if (!marketCondition.IsFavorable)
            {
                return false;
            }

            // Strong buy signals create automatic entry
            if (orderBookSignal == OrderBookSignal.StrongBuy || marketCondition.TrendDirection == TrendDirection.Up)
            {
                return true;
            }

            // If price is near strong support, favor entry
            bool nearStrongSupport = priceLevels
                .Where(p => p.Type == "Support" && p.Strength >= 3)
                .Any();

            if (nearStrongSupport && marketCondition.RSI < 65)
            {
                return true;
            }

            // If momentum is good, enter
            if (marketCondition.RSI >= 30 && marketCondition.RSI <= 60 && marketCondition.TrendDirection != TrendDirection.Down)
            {
                return true;
            }

            // Default to more conservative
            return false;
        }

        private static decimal CalculateQuantity(decimal budget, decimal price, decimal stepSize, decimal minQty)
        {
            // Calculate the maximum quantity we can buy with the budget
            decimal maxQuantity = budget / price;

            // Make sure we're above the minimum quantity
            if (maxQuantity < minQty)
            {
                return 0; // Can't afford minimum quantity
            }

            // Apply a small buffer to account for price movements (0.5%)
            maxQuantity *= 0.995m;

            // Adjust quantity to step size - round down to nearest valid increment
            decimal adjustedQuantity = Math.Floor(maxQuantity / stepSize) * stepSize;

            return adjustedQuantity;
        }

        private static async Task<WebCallResult<BinancePlacedOrder>> PlaceOrder(BinanceRestClient client, string symbol, OrderSide side, decimal quantity)
        {
            try
            {
                string actionText = side == OrderSide.Buy ? "Buy" : "Sell";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Placing {actionText} MARKET order for {symbol}, Quantity: {quantity.ToString("0.00000000")}");

                var result = await client.SpotApi.Trading.PlaceOrderAsync(
                    symbol,
                    side,
                    SpotOrderType.Market,
                    quantity: quantity);

                if (result.Success)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {actionText} order executed successfully. ID: {result.Data.Id}");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {actionText} order FAILED: {result.Error?.Message}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Exception placing {side} order: {ex.Message}");

                // Fixed error creation
                return new WebCallResult<BinancePlacedOrder>(new UnknownError(ex.Message));
            }
        }

        private static async Task<WebCallResult<BinancePlacedOrder>> PlaceLimitOrder(
            BinanceRestClient client,
            string symbol,
            OrderSide side,
            decimal quantity,
            decimal price)
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Preparing {side} LIMIT order for {symbol} at {price.ToString("0.00000000000")}");

                var result = await client.SpotApi.Trading.PlaceOrderAsync(
                    symbol,
                    side,
                    SpotOrderType.Limit,
                    quantity: quantity,
                    price: price,
                    timeInForce: TimeInForce.GoodTillCanceled);

                if (result.Success)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit order placed successfully (ID: {result.Data.Id}), waiting for fill (timeout: 10s)");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit order FAILED: {result.Error?.Message}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Exception placing limit order: {ex.Message}");

                // Fixed error creation
                return new WebCallResult<BinancePlacedOrder>(new UnknownError(ex.Message));
            }
        }

        private static async Task<bool> WaitForOrderFill(BinanceRestClient client, string symbol, long orderId, TimeSpan timeout)
        {
            var startTime = DateTime.Now;

            while (DateTime.Now - startTime < timeout)
            {
                try
                {
                    var orderStatus = await client.SpotApi.Trading.GetOrderAsync(symbol, orderId);

                    if (orderStatus.Success)
                    {
                        if (orderStatus.Data.Status == OrderStatus.Filled)
                        {
                            return true; // Order filled
                        }

                        if (orderStatus.Data.Status == OrderStatus.Canceled || orderStatus.Data.Status == OrderStatus.Rejected || orderStatus.Data.Status == OrderStatus.Expired)
                        {
                            return false; // Order canceled/rejected
                        }
                    }

                    await Task.Delay(500); // Check every 500ms
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error checking order status: {ex.Message}");
                    await Task.Delay(1000); // Wait longer on error
                }
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit order timeout reached, cancelling order");
            return false; // Timeout reached
        }

        private static async Task<bool> IsOrderComplete(BinanceRestClient client, string symbol, long orderId)
        {
            try
            {
                var orderStatus = await client.SpotApi.Trading.GetOrderAsync(symbol, orderId);

                if (orderStatus.Success)
                {
                    return orderStatus.Data.Status == OrderStatus.Filled;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // Method to get the actual balance of an asset
        private static async Task<decimal> GetActualBalance(BinanceRestClient client, string symbol)
        {
            try
            {
                // Extract the base asset (the part before USDT)
                string baseAsset = symbol.Replace("USDT", "");

                // Get account information
                var accountInfo = await client.SpotApi.Account.GetAccountInfoAsync();
                if (!accountInfo.Success)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to get account info: {accountInfo.Error?.Message}");
                    return 0;
                }

                // Find the balance for this asset
                var asset = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == baseAsset);
                if (asset == null)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Asset {baseAsset} not found in balances");
                    return 0;
                }

                // Return available balance
                decimal availableBalance = asset.Available;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Actual available balance for {baseAsset}: {availableBalance}");
                return availableBalance;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error getting actual balance: {ex.Message}");
                return 0;
            }
        }
    }
}