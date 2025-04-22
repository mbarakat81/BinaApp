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

        // Dictionary to track coins in cooldown period after a trade
        private static readonly ConcurrentDictionary<string, DateTime> _tradeCooldownPeriods =
            new ConcurrentDictionary<string, DateTime>();

        // Dictionary to track coins in cooldown after unfavorable conditions
        private static readonly ConcurrentDictionary<string, (DateTime Until, string Reason)> _unfavorableConditionCooldowns =
            new ConcurrentDictionary<string, (DateTime, string)>();

        // Dictionary to track last exit prices for each symbol
        private static readonly ConcurrentDictionary<string, decimal> _lastExitPrices =
            new ConcurrentDictionary<string, decimal>();

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
            // Check low volatility cooldown
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

            // Check unfavorable condition cooldown
            if (_unfavorableConditionCooldowns.TryGetValue(symbol, out var cooldownInfo))
            {
                if (DateTime.Now < cooldownInfo.Until)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Skipping {symbol} due to {cooldownInfo.Reason}. Will check again after {cooldownInfo.Until:HH:mm:ss}");
                    return true;
                }
                else
                {
                    // Time has passed, remove from skip list
                    _unfavorableConditionCooldowns.TryRemove(symbol, out _);
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

        // Mark a symbol for replacement due to unfavorable conditions
        public static void MarkForReplacement(string symbol, string reason, int cooldownMinutes = 30)
        {
            DateTime cooldownUntil = DateTime.Now.AddMinutes(cooldownMinutes);
            _unfavorableConditionCooldowns.AddOrUpdate(
                symbol,
                (cooldownUntil, reason),
                (key, oldValue) => (cooldownUntil, reason)
            );
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Marking {symbol} for replacement due to: {reason}. Cooldown until {cooldownUntil:HH:mm:ss}");
        }

        // Get list of coins marked for replacement
        public static List<string> GetSymbolsMarkedForReplacement()
        {
            return _unfavorableConditionCooldowns
                .Where(kvp => DateTime.Now < kvp.Value.Until)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        // Check if a coin is in cooldown period after a trade
        private static bool IsInCooldownPeriod(string symbol)
        {
            if (_tradeCooldownPeriods.TryGetValue(symbol, out DateTime cooldownUntil))
            {
                if (DateTime.Now < cooldownUntil)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {symbol} in cooling off period until {cooldownUntil:HH:mm:ss}");
                    return true;
                }
                else
                {
                    // Cooldown period has passed, remove from dictionary
                    _tradeCooldownPeriods.TryRemove(symbol, out _);
                }
            }
            return false;
        }

        // Place symbol in cooldown after a trade
        private static void PlaceSymbolInCooldown(string symbol, int minutes = 20)
        {
            DateTime cooldownUntil = DateTime.Now.AddMinutes(minutes);
            _tradeCooldownPeriods.AddOrUpdate(symbol, cooldownUntil, (key, oldValue) => cooldownUntil);
        }

        // Record the last exit price for a symbol
        private static void RecordExitPrice(string symbol, decimal exitPrice)
        {
            _lastExitPrices.AddOrUpdate(symbol, exitPrice, (key, oldValue) => exitPrice);
        }

        // Check if current price is favorable compared to last exit price
        private static bool IsPriceFavorableToLastExit(string symbol, decimal currentPrice)
        {
            if (_lastExitPrices.TryGetValue(symbol, out decimal lastExitPrice))
            {
                // Only consider the price favorable if it's at least 0.5% below last exit price
                bool isFavorable = currentPrice <= lastExitPrice * 0.995m;

                if (!isFavorable)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Current price ({currentPrice}) is above or too close to last exit price ({lastExitPrice})");
                }

                return isFavorable;
            }
            return true; // No previous exit price recorded, so price is considered favorable
        }

        // Get historical uptrend high price for a symbol
        private static decimal GetHistoricalUptrendHighPrice(string symbol)
        {
            // Return 0 if we don't have data for this symbol
            if (!_uptrendHighs.TryGetValue(symbol, out var highs) || highs.Count == 0)
            {
                return 0;
            }

            // Return the maximum high if it's greater than current price
            decimal max = highs.Max();
            return max;
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

            // Check if symbol should be skipped due to previously detected extremely low volatility or other cooldowns
            if (ShouldSkipSymbol(symbol))
            {
                result.ErrorMessage = "Symbol in cooldown period - skipping";
                return result;
            }

            // Check if symbol is in cooldown period after a recent trade
            if (IsInCooldownPeriod(symbol))
            {
                result.ErrorMessage = "Symbol in cooling off period after recent trade";
                return result;
            }

            // Check for extremely low volatility - if found, mark to skip for 2 hours
            if (marketCondition.RSIVolatility < EXTREMELY_LOW_VOLATILITY_THRESHOLD)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Detected extremely low volatility for {symbol}: {marketCondition.RSIVolatility:F3}%");
                MarkSymbolLowVolatility(symbol);
                result.ErrorMessage = "Extremely low volatility detected, skipping symbol for 2 hours";
                result.ShouldReplaceSymbol = true;
                return result;
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

            // Check if current price is favorable compared to the last exit price
            if (!IsPriceFavorableToLastExit(symbol, currentPrice))
            {
                result.ErrorMessage = "Current price unfavorable compared to last exit price";
                return result;
            }

            // Initialize trading parameters from market conditions

            // Adjust stop loss based on volatility - more volatile assets get wider stops
            decimal stopLossPercent = Math.Max(1.2m, Math.Min(3.0m, marketCondition.RSIVolatility * 3.0m));
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Dynamic stop loss set at {stopLossPercent:F2}% based on volatility of {marketCondition.RSIVolatility:F2}%");

            // Adjust take profit based on volatility and market conditions
            decimal takeProfitPercent = Math.Max(0.8m, marketCondition.TakeProfitPercent);

            // Make sure stop loss is reasonable
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Strategy execution started for {symbol}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Stop loss set at {stopLossPercent:F2}%, Take profit at {takeProfitPercent:F2}%");

            // Dynamic adjustment based on market conditions
            if (marketCondition.RSIVolatility < 0.3m)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Low volatility detected ({marketCondition.RSIVolatility:F2}%), adjusting parameters");
                takeProfitPercent = Math.Max(0.6m, takeProfitPercent * 0.8m);
            }

            // Check support/resistance levels
            bool nearStrongSupport = priceLevels
                .Where(p => p.Type == "Support" && p.Strength >= 3)
                .Any();

            bool nearStrongResistance = priceLevels
                .Where(p => p.Type == "Resistance" && p.Strength >= 3)
                .Any();

            if (nearStrongResistance)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Price near strong resistance - avoiding entry");
                result.ErrorMessage = "Price is near strong resistance - avoiding entry";
                result.ShouldReplaceSymbol = true;
                MarkForReplacement(symbol, "Price near strong resistance", 20);
                return result;
            }

            if (nearStrongSupport)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Price near strong support, increasing target profit");
                takeProfitPercent *= 1.2m; // Increase take profit when near support
            }

            // NEW: Advanced entry analysis 
            var entryAnalysis = await AnalyzeEntryOpportunity(client, symbol, marketCondition, priceLevels);
            bool shouldEnter = entryAnalysis.ShouldEnter;
            decimal targetEntryPrice = entryAnalysis.IdealPrice;

            if (!shouldEnter)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entry criteria not met: {entryAnalysis.Reason}");
                result.ErrorMessage = $"Entry criteria not met: {entryAnalysis.Reason}";
                result.ShouldReplaceSymbol = true;
                MarkForReplacement(symbol, entryAnalysis.Reason, 15);
                return result;
            }

            // If we get here, entry analysis suggests a good opportunity
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Found entry opportunity: {entryAnalysis.Reason}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Target entry price: {targetEntryPrice} (current: {currentPrice})");

            // Calculate quantity based on budget and current price
            decimal quantity = CalculateQuantity(budget, currentPrice, stepSize, minQuantity);

            // Ensure minimum quantity is met
            if (quantity < minQuantity)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Calculated quantity ({quantity}) is less than minimum ({minQuantity}). Adjusting to minimum.");
                quantity = minQuantity;
            }

            // Check if calculated order value meets exchange minimums
            if (quantity * currentPrice < 6)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Order value ({quantity * currentPrice:F2} USDT) is too small. Exchange minimum is typically 6 USDT.");
                result.ErrorMessage = "Order value too small";
                return result;
            }

            // Log quantity to be purchased
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Planning to buy {quantity} {symbol} at approximately {targetEntryPrice}");

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
            long? takeProfitOrderId = null; // To track our take profit order

            // Calculate the maximum wait time based on market trend and proximity to target price
            TimeSpan maxWaitTime;
            decimal priceGap = Math.Abs((targetEntryPrice - currentPrice) / currentPrice) * 100;

            if (marketCondition.TrendDirection == TrendDirection.Up)
            {
                // In uptrends, we don't wait too long to avoid missing the move
                if (priceGap < 0.5m)
                    maxWaitTime = TimeSpan.FromMinutes(3);
                else
                    maxWaitTime = TimeSpan.FromMinutes(5);
            }
            else if (marketCondition.TrendDirection == TrendDirection.Down)
            {
                // In downtrends, we can wait longer for better entries
                if (priceGap < 0.5m)
                    maxWaitTime = TimeSpan.FromMinutes(8);
                else
                    maxWaitTime = TimeSpan.FromMinutes(15);
            }
            else // Sideways
            {
                maxWaitTime = TimeSpan.FromMinutes(10);
            }

            // ----- BEGIN OPTIMIZED ENTRY LOGIC -----

            // Track entry time and order attempts
            DateTime entryStartTime = DateTime.Now;
            List<long> currentOrderIds = new List<long>(); // Track all placed orders
            DateTime lastOrderUpdate = DateTime.Now;
            decimal lowestSeenPrice = currentPrice;
            int buyAttempts = 0;
            bool priceUpdated = false;

            // Initial entry price calculation
            decimal buyLimitPrice = targetEntryPrice;

            // Round limit price to tick size
            buyLimitPrice = Math.Floor(buyLimitPrice / tickSize) * tickSize;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Starting entry process with initial target price: {buyLimitPrice}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Maximum wait time: {maxWaitTime.TotalMinutes:F1} minutes");

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

                    // Check if any of our orders have been filled
                    if (currentOrderIds.Count > 0)
                    {
                        bool anyOrderFilled = false;
                        long? filledOrderId = null;
                        decimal filledPrice = 0;

                        foreach (var orderId in currentOrderIds.ToList()) // Use ToList() to avoid collection modified exception
                        {
                            var orderStatus = await client.SpotApi.Trading.GetOrderAsync(symbol, orderId);
                            if (orderStatus.Success && orderStatus.Data.Status == OrderStatus.Filled)
                            {
                                anyOrderFilled = true;
                                filledOrderId = orderId;
                                filledPrice = orderStatus.Data.Price;
                                break;
                            }
                        }

                        if (anyOrderFilled)
                        {
                            // Cancel all other open orders
                            foreach (var orderId in currentOrderIds)
                            {
                                if (orderId != filledOrderId)
                                {
                                    try
                                    {
                                        await client.SpotApi.Trading.CancelOrderAsync(symbol, orderId);
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cancelled order {orderId} as another order was filled");
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling order {orderId}: {ex.Message}");
                                    }
                                }
                            }

                            // Position has been entered
                            inPosition = true;
                            buyPrice = filledPrice;
                            highestPrice = buyPrice;
                            lowestPrice = buyPrice;
                            totalBuyValue = quantity * buyPrice;
                            trailingStopCheckCount = 0;

                            result.EntryPrice = buyPrice;
                            result.EntryTime = DateTime.Now;
                            result.Quantity = quantity;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Order filled at {buyPrice}. Position entered.");

                            // Immediately place a take profit order
                            decimal takeProfitPrice = buyPrice * (1 + takeProfitPercent / 100);
                            takeProfitPrice = Math.Floor(takeProfitPrice / tickSize) * tickSize;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Setting immediate take profit order at {takeProfitPrice} ({takeProfitPercent:F2}% above entry)");

                            var tpOrderResult = await PlaceLimitOrder(client, symbol, OrderSide.Sell, quantity, takeProfitPrice);
                            if (tpOrderResult.Success)
                            {
                                takeProfitOrderId = tpOrderResult.Data.Id;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit order placed successfully (ID: {takeProfitOrderId})");
                            }
                            else
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to place take profit order: {tpOrderResult.Error?.Message}");
                            }

                            // Clear the buy orders list since we're in position
                            currentOrderIds.Clear();
                            break;
                        }
                    }

                    // Update lowest seen price if price drops
                    if (currentPrice < lowestSeenPrice)
                    {
                        lowestSeenPrice = currentPrice;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] New lowest price: {lowestSeenPrice}");
                        priceUpdated = true;
                    }

                    // Dynamic order adjustment based on price movement and time
                    if (currentOrderIds.Count == 0 ||
                        (priceUpdated && (DateTime.Now - lastOrderUpdate).TotalSeconds > 15))
                    {
                        // If we need to replace existing orders, cancel them first
                        foreach (var orderId in currentOrderIds.ToList())
                        {
                            try
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Canceling existing order {orderId}");
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, orderId);
                                currentOrderIds.Remove(orderId);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling order {orderId}: {ex.Message}");
                            }
                        }

                        // Calculate optimal entry price based on current conditions
                        decimal entryAdjustedPrice;

                        // Adjust strategy based on time elapsed and current price relative to target
                        TimeSpan elapsedTime = DateTime.Now - entryStartTime;
                        double timeRatio = elapsedTime.TotalMilliseconds / maxWaitTime.TotalMilliseconds;

                        // If current price is already below our original target, use it
                        if (currentPrice <= targetEntryPrice)
                        {
                            entryAdjustedPrice = currentPrice;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Current price {currentPrice} is at or below target entry {targetEntryPrice} - placing order immediately");
                        }
                        // Otherwise, adjust our limit price based on elapsed time
                        else if (timeRatio < 0.3) // First 30% of wait time
                        {
                            // Be patient and aim for our ideal entry price
                            entryAdjustedPrice = targetEntryPrice;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Early phase - targeting ideal entry price: {entryAdjustedPrice}");
                        }
                        else if (timeRatio < 0.7) // 30-70% of wait time
                        {
                            // Start to compromise, get halfway between target and current
                            entryAdjustedPrice = (targetEntryPrice + currentPrice) / 2;

                            // Ensure we're at least getting some discount from current price
                            entryAdjustedPrice = Math.Min(entryAdjustedPrice, currentPrice * 0.998m);

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Middle phase - adjusting target to: {entryAdjustedPrice}");
                        }
                        else // Final 30% of wait time
                        {
                            // Get more aggressive to ensure entry, but still try for a small discount
                            entryAdjustedPrice = currentPrice * 0.999m; // 0.1% below current price
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Final phase - ensuring entry with small discount: {entryAdjustedPrice}");
                        }

                        // Apply tick size rounding
                        entryAdjustedPrice = Math.Floor(entryAdjustedPrice / tickSize) * tickSize;
                        buyLimitPrice = entryAdjustedPrice;

                        // Place the new limit order
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Placing limit buy at {buyLimitPrice}, quantity: {quantity}");
                        var limitOrderResult = await PlaceLimitOrder(client, symbol, OrderSide.Buy, quantity, buyLimitPrice);

                        if (limitOrderResult.Success)
                        {
                            currentOrderIds.Add(limitOrderResult.Data.Id);
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

                                // Immediately place a take profit order
                                decimal takeProfitPrice = buyPrice * (1 + takeProfitPercent / 100);
                                takeProfitPrice = Math.Floor(takeProfitPrice / tickSize) * tickSize;

                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Setting immediate take profit order at {takeProfitPrice} ({takeProfitPercent:F2}% above entry)");

                                var tpOrderResult = await PlaceLimitOrder(client, symbol, OrderSide.Sell, quantity, takeProfitPrice);
                                if (tpOrderResult.Success)
                                {
                                    takeProfitOrderId = tpOrderResult.Data.Id;
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit order placed successfully (ID: {takeProfitOrderId})");
                                }
                                else
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to place take profit order: {tpOrderResult.Error?.Message}");
                                }

                                // Clear the buy orders list
                                currentOrderIds.Clear();
                                break;
                            }
                        }
                        else
                        {
                            // If order placement failed, log error and maybe try market order
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to place limit order: {limitOrderResult.Error?.Message}");

                            // If we're running out of time or had multiple failures, try market order
                            if (timeRatio > 0.9 || buyAttempts > 5)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Limit order failed, switching to market order");

                                // Place market order 
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

                                    // Immediately place a take profit order
                                    decimal takeProfitPrice = buyPrice * (1 + takeProfitPercent / 100);
                                    takeProfitPrice = Math.Floor(takeProfitPrice / tickSize) * tickSize;

                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Setting immediate take profit order at {takeProfitPrice} ({takeProfitPercent:F2}% above entry)");

                                    var tpOrderResult = await PlaceLimitOrder(client, symbol, OrderSide.Sell, quantity, takeProfitPrice);
                                    if (tpOrderResult.Success)
                                    {
                                        takeProfitOrderId = tpOrderResult.Data.Id;
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit order placed successfully (ID: {takeProfitOrderId})");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to place take profit order: {tpOrderResult.Error?.Message}");
                                    }

                                    break;
                                }
                            }
                        }
                    }

                    // If max wait time almost reached, ensure execution with market order
                    if ((maxWaitTime - (DateTime.Now - entryStartTime)).TotalSeconds < 10 && !inPosition)
                    {
                        // Cancel all existing orders
                        foreach (var orderId in currentOrderIds.ToList())
                        {
                            try
                            {
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, orderId);
                                currentOrderIds.Remove(orderId);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling order {orderId}: {ex.Message}");
                            }
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

                            // Immediately place a take profit order
                            decimal takeProfitPrice = buyPrice * (1 + takeProfitPercent / 100);
                            takeProfitPrice = Math.Floor(takeProfitPrice / tickSize) * tickSize;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Setting immediate take profit order at {takeProfitPrice} ({takeProfitPercent:F2}% above entry)");

                            var tpOrderResult = await PlaceLimitOrder(client, symbol, OrderSide.Sell, quantity, takeProfitPrice);
                            if (tpOrderResult.Success)
                            {
                                takeProfitOrderId = tpOrderResult.Data.Id;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit order placed successfully (ID: {takeProfitOrderId})");
                            }
                            else
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to place take profit order: {tpOrderResult.Error?.Message}");
                            }

                            break;
                        }
                    }

                    // Short delay between price checks
                    await Task.Delay(1000);
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
                // Cancel any existing orders
                foreach (var orderId in currentOrderIds.ToList())
                {
                    try
                    {
                        await client.SpotApi.Trading.CancelOrderAsync(symbol, orderId);
                        currentOrderIds.Remove(orderId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling order {orderId}: {ex.Message}");
                    }
                }

                // Perform one final market check to see if entry is still favorable
                var entryCheckAnalysis = await AnalyzeEntryOpportunity(client, symbol, marketCondition, priceLevels);

                // Final check to see if the entry is still favorable
                // If we've waited this long and the opportunity is gone, let's bail
                if (!entryCheckAnalysis.ShouldEnter)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entry opportunity no longer favorable: {entryCheckAnalysis.Reason}");
                    result.ErrorMessage = $"Entry opportunity expired: {entryCheckAnalysis.Reason}";
                    result.ShouldReplaceSymbol = true;
                    return result;
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

                    // Immediately place a take profit order
                    decimal takeProfitPrice = buyPrice * (1 + takeProfitPercent / 100);
                    takeProfitPrice = Math.Floor(takeProfitPrice / tickSize) * tickSize;

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Setting immediate take profit order at {takeProfitPrice} ({takeProfitPercent:F2}% above entry)");

                    var tpOrderResult = await PlaceLimitOrder(client, symbol, OrderSide.Sell, quantity, takeProfitPrice);
                    if (tpOrderResult.Success)
                    {
                        takeProfitOrderId = tpOrderResult.Data.Id;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit order placed successfully (ID: {takeProfitOrderId})");
                    }
                    else
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to place take profit order: {tpOrderResult.Error?.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to enter position: {emergencyMarketOrder.Error?.Message}");
                    result.ErrorMessage = "Failed to enter position after multiple attempts";
                    return result;
                }
            }

            // ----- END OPTIMIZED ENTRY LOGIC -----

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

                    // Cancel and replace take profit order if one exists
                    if (takeProfitOrderId.HasValue)
                    {
                        try
                        {
                            await client.SpotApi.Trading.CancelOrderAsync(symbol, takeProfitOrderId.Value);

                            // Place new take profit with adjusted quantity
                            decimal takeProfitPrice = buyPrice * (1 + takeProfitPercent / 100);
                            takeProfitPrice = Math.Floor(takeProfitPrice / tickSize) * tickSize;

                            var newTpOrder = await PlaceLimitOrder(client, symbol, OrderSide.Sell, quantity, takeProfitPrice);
                            if (newTpOrder.Success)
                            {
                                takeProfitOrderId = newTpOrder.Data.Id;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit order replaced with adjusted quantity (ID: {takeProfitOrderId})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error adjusting take profit order: {ex.Message}");
                        }
                    }
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
            DateTime priorityMonitoringStart = DateTime.Now; // Track when we started priority monitoring
            bool inPriorityMonitoring = true; // Start in priority monitoring mode

            // Trailing take profit variables
            bool trailingTakeProfitActive = false;
            decimal trailingStopPrice = 0;
            decimal trailingReferencePrice = 0;
            decimal newTakeProfitPrice = 0;

            // Variables for progressive trailing stop adjustment
            int profitTargetHitCount = 0;
            decimal initialBuyPrice = buyPrice; // Store original buy price
            decimal adjustedBuyPrice = buyPrice; // This will be updated as we secure profits

            // NEW: Identify key resistance levels above our entry price for selling purposes
            var potentialResistanceLevels = priceLevels
                .Where(p => p.Type == "Resistance" && p.Price > buyPrice)
                .OrderBy(p => p.Price)
                .ToList();

            // Calculate dynamic take profit based on nearest resistance
            decimal dynamicTakeProfitPrice = 0;
            decimal distanceToResistance = 0;

            if (potentialResistanceLevels.Any())
            {
                // Find the nearest resistance level above our entry
                var nearestResistance = potentialResistanceLevels.First();

                // Calculate distance to resistance as percentage
                distanceToResistance = (nearestResistance.Price - buyPrice) / buyPrice * 100;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Nearest resistance at {nearestResistance.Price} " +
                                  $"({distanceToResistance:F2}% above entry)");

                // If resistance is close (less than 2x our take profit target), use it to inform our target
                if (distanceToResistance < takeProfitPercent * 2)
                {
                    // Target 90% of the distance to resistance to avoid selling right at resistance
                    // which often has selling pressure and can cause rejections
                    dynamicTakeProfitPrice = buyPrice * (1 + (distanceToResistance * 0.9m) / 100);

                    // If this is higher than our default take profit, use it
                    if (dynamicTakeProfitPrice > buyPrice * (1 + takeProfitPercent / 100))
                    {
                        decimal originalTakeProfitPrice = buyPrice * (1 + takeProfitPercent / 100);

                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Adjusted take profit based on resistance: " +
                                        $"{dynamicTakeProfitPrice} (was {originalTakeProfitPrice})");

                        takeProfitPercent = distanceToResistance * 0.9m;

                        // Update the take profit order if it exists
                        if (takeProfitOrderId.HasValue)
                        {
                            try
                            {
                                // Cancel the existing take profit order
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, takeProfitOrderId.Value);

                                // Place the updated take profit order
                                decimal updatedTakeProfitPrice = Math.Floor(dynamicTakeProfitPrice / tickSize) * tickSize;
                                var updatedTpOrder = await PlaceLimitOrder(client, symbol, OrderSide.Sell, quantity, updatedTakeProfitPrice);

                                if (updatedTpOrder.Success)
                                {
                                    takeProfitOrderId = updatedTpOrder.Data.Id;
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Updated take profit order with resistance-based price: {updatedTakeProfitPrice}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error updating take profit order: {ex.Message}");
                            }
                        }
                    }
                }
                // If resistance is far away, we might want to be more aggressive with trailing stops
                else if (distanceToResistance > takeProfitPercent * 3)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Wide range to resistance detected - using enhanced trailing strategy");
                    // We'll handle this in the trailing stop logic
                }
            }

            while (inPosition)
            {
                try
                {
                    // First, check if take profit order has been filled
                    if (takeProfitOrderId.HasValue)
                    {
                        var tpOrderStatus = await client.SpotApi.Trading.GetOrderAsync(symbol, takeProfitOrderId.Value);
                        if (tpOrderStatus.Success && tpOrderStatus.Data.Status == OrderStatus.Filled)
                        {
                            // Take profit order has been filled - exit the position
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Take profit order filled at {tpOrderStatus.Data.Price}");

                            inPosition = false;

                            // Record the exit price
                            RecordExitPrice(symbol, tpOrderStatus.Data.Price);

                            // Place symbol in cooldown
                            PlaceSymbolInCooldown(symbol);

                            result.Success = true;
                            result.ExitPrice = tpOrderStatus.Data.Price;
                            result.ExitTime = DateTime.Now;
                            result.ExitReason = "Take Profit (Limit Order)";

                            // Calculate profit
                            decimal profit = quantity * (tpOrderStatus.Data.Price - buyPrice);

                            // Subtract fees
                            decimal estimatedFees = (quantity * buyPrice * makerFee) + (quantity * tpOrderStatus.Data.Price * makerFee);
                            profit -= estimatedFees;

                            result.Profit = profit;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Position closed with profit: {profit:F8} USDT");

                            return result;
                        }
                    }

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

                    // Check if we should switch between priority and normal monitoring
                    if (inPriorityMonitoring && (DateTime.Now - priorityMonitoringStart).TotalMinutes > 5)
                    {
                        // After 5 minutes of priority monitoring, we can switch to normal mode
                        inPriorityMonitoring = false;
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Switching from priority to normal monitoring after 5 minutes");

                        // Signal that this position can be momentarily deprioritized
                        result.CanDeprioritize = true;
                        return result;
                    }

                    // ENHANCED TRAILING TAKE PROFIT LOGIC
                    if (currentProfitPercent > 0 && currentProfitPercent >= takeProfitPercent)
                    {
                        // Cancel any existing take profit order to implement dynamic trailing
                        if (takeProfitOrderId.HasValue)
                        {
                            try
                            {
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, takeProfitOrderId.Value);
                                takeProfitOrderId = null;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cancelled static take profit order to implement trailing take profit");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling take profit order: {ex.Message}");
                            }
                        }

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

                                // Record the exit price
                                RecordExitPrice(symbol, currentPrice);

                                // Place symbol in cooldown
                                PlaceSymbolInCooldown(symbol);

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

                                // NEW: Set dynamic trailing percentage based on volatility and S/R range
                                decimal trailingPercent;

                                // If we're in a tight range between support and resistance
                                if (distanceToResistance > 0 && distanceToResistance < 5)
                                {
                                    // Use tighter trailing stop to secure profits in confined range
                                    trailingPercent = Math.Max(0.7m, Math.Min(1.2m, currentProfitPercent * 0.20m));
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Tight range to resistance - using narrow {trailingPercent:F2}% trailing stop");
                                }
                                // If we're in a wide range with lots of room to run
                                else if (distanceToResistance > takeProfitPercent * 3)
                                {
                                    // Use wider trailing stop to capture more of a potential run
                                    trailingPercent = Math.Max(1.0m, Math.Min(2.0m, currentProfitPercent * 0.30m));
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Wide range to resistance - using wider {trailingPercent:F2}% trailing stop");
                                }
                                else
                                {
                                    // Default case - balanced approach
                                    trailingPercent = Math.Max(0.8m, Math.Min(1.5m, currentProfitPercent * 0.25m));
                                }

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
                            else if (currentPrice > trailingReferencePrice * 1.005m) // Only update on 0.5% moves up
                            {
                                // Calculate how much the price has moved up
                                decimal moveUpPercent = (currentPrice - trailingReferencePrice) / trailingReferencePrice * 100;

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
                                decimal trailingPercent;

                                // NEW - Adjust trailing stop based on resistance proximity
                                if (potentialResistanceLevels.Any())
                                {
                                    var nearestResistance = potentialResistanceLevels.First();
                                    decimal percentToResistance = (nearestResistance.Price - currentPrice) / currentPrice * 100;

                                    if (percentToResistance < 1.0m)
                                    {
                                        // Very close to resistance - tighten trailing stop significantly
                                        trailingPercent = Math.Max(0.5m, Math.Min(0.8m, currentProfitPercent * 0.15m));
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Very close to resistance ({percentToResistance:F2}% away) - using tight {trailingPercent:F2}% trailing stop");
                                    }
                                    else if (percentToResistance < 3.0m)
                                    {
                                        // Approaching resistance - moderate trailing stop
                                        trailingPercent = Math.Max(0.7m, Math.Min(1.2m, currentProfitPercent * 0.2m));
                                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Approaching resistance ({percentToResistance:F2}% away) - using moderate {trailingPercent:F2}% trailing stop");
                                    }
                                    else
                                    {
                                        // Far from resistance - standard trailing stop
                                        trailingPercent = Math.Max(0.8m, Math.Min(1.5m, currentProfitPercent * 0.2m));
                                    }
                                }
                                else
                                {
                                    // No resistance detected - use standard approach
                                    trailingPercent = Math.Max(0.8m, Math.Min(1.5m, currentProfitPercent * 0.2m));
                                }

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

                    // Check if trailing stop is triggered
                    if (trailingTakeProfitActive && currentPrice <= trailingStopPrice)
                    {
                        // Cancel any existing take profit order
                        if (takeProfitOrderId.HasValue)
                        {
                            try
                            {
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, takeProfitOrderId.Value);
                                takeProfitOrderId = null;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cancelled static take profit order to execute trailing stop");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling take profit order: {ex.Message}");
                            }
                        }

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

                            // Record the exit price
                            RecordExitPrice(symbol, currentPrice);

                            // Place symbol in cooldown
                            PlaceSymbolInCooldown(symbol);

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
                        // Cancel any existing take profit order
                        if (takeProfitOrderId.HasValue)
                        {
                            try
                            {
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, takeProfitOrderId.Value);
                                takeProfitOrderId = null;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cancelled take profit order to execute stop loss");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling take profit order: {ex.Message}");
                            }
                        }

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

                            // Record the exit price
                            RecordExitPrice(symbol, currentPrice);

                            // Place symbol in cooldown with longer duration for stop loss
                            PlaceSymbolInCooldown(symbol, 30); // 30 minutes cooldown after stop loss

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
                        // NEW - Check for resistance proximity to inform time-based exit
                        bool nearResistanceForTimeExit = false;

                        if (potentialResistanceLevels.Any())
                        {
                            var nearestResistance = potentialResistanceLevels.First();
                            decimal percentToResistance = (nearestResistance.Price - currentPrice) / currentPrice * 100;

                            if (percentToResistance < 0.5m && currentProfitPercent > 0.5m)
                            {
                                nearResistanceForTimeExit = true;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Time-based exit triggered after {timeInPosition.TotalMinutes:F1} minutes " +
                                                $"with {currentProfitPercent:F2}% profit - near resistance at {nearestResistance.Price}");
                            }
                        }

                        if (nearResistanceForTimeExit || timeInPosition.TotalMinutes > 25)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Time-based exit triggered after {timeInPosition.TotalMinutes:F1} minutes with {currentProfitPercent:F2}% profit");

                            // Cancel any existing take profit order
                            if (takeProfitOrderId.HasValue)
                            {
                                try
                                {
                                    await client.SpotApi.Trading.CancelOrderAsync(symbol, takeProfitOrderId.Value);
                                    takeProfitOrderId = null;
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cancelled take profit order for time-based exit");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling take profit order: {ex.Message}");
                                }
                            }

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

                                // Record the exit price
                                RecordExitPrice(symbol, currentPrice);

                                // Place symbol in cooldown
                                PlaceSymbolInCooldown(symbol);

                                result.Success = true;
                                result.ExitPrice = currentPrice;
                                result.ExitTime = DateTime.Now;
                                result.ExitReason = nearResistanceForTimeExit ?
                                    "Time Based Exit (Near Resistance)" : "Time Based Exit";

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
                    }

                    // Maximum time in trade - cut losses
                    if (timeInPosition.TotalMinutes > 40)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Maximum time in trade reached ({timeInPosition.TotalMinutes:F1} min). Exiting position.");

                        // Cancel any existing take profit order
                        if (takeProfitOrderId.HasValue)
                        {
                            try
                            {
                                await client.SpotApi.Trading.CancelOrderAsync(symbol, takeProfitOrderId.Value);
                                takeProfitOrderId = null;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cancelled take profit order for max time exit");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error cancelling take profit order: {ex.Message}");
                            }
                        }

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

                            // Record the exit price
                            RecordExitPrice(symbol, currentPrice);

                            // Place symbol in cooldown
                            PlaceSymbolInCooldown(symbol);

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

        private static async Task<(bool ShouldEnter, decimal IdealPrice, string Reason)> AnalyzeEntryOpportunity(
    BinanceRestClient client,
    string symbol,
    MarketCondition marketCondition,
    List<KeyPriceLevel> priceLevels)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Performing detailed entry analysis for {symbol}...");

            // Get current price
            var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
            if (!ticker.Success)
            {
                return (false, 0, "Failed to get current price");
            }
            decimal currentPrice = ticker.Data.LastPrice;

            // Get historical candles for multiple timeframes
            var oneMinCandles = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneMinute, limit: 100);
            var fiveMinCandles = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FiveMinutes, limit: 60);
            var fifteenMinCandles = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FifteenMinutes, limit: 48);
            var hourlyCandles = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.OneHour, limit: 24);
            var fourHourCandles = await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, KlineInterval.FourHour, limit: 30);

            if (!oneMinCandles.Success || !fiveMinCandles.Success || !fifteenMinCandles.Success ||
                !hourlyCandles.Success || !fourHourCandles.Success)
            {
                return (false, 0, "Failed to fetch historical price data");
            }

            // --- DETECT LOCAL HIGHS AND OVEREXTENDED PRICE ---

            // Get highest highs from each timeframe
            decimal recentOneMinHigh = oneMinCandles.Data.Take(8).Max(c => c.HighPrice);
            decimal recentFiveMinHigh = fiveMinCandles.Data.Take(6).Max(c => c.HighPrice);
            decimal recentFifteenMinHigh = fifteenMinCandles.Data.Take(4).Max(c => c.HighPrice);
            decimal recentHourlyHigh = hourlyCandles.Data.Take(4).Max(c => c.HighPrice);

            // Calculate how close current price is to recent highs (in percentage)
            decimal oneMinHighProximity = (currentPrice / recentOneMinHigh) * 100;
            decimal fiveMinHighProximity = (currentPrice / recentFiveMinHigh) * 100;
            decimal fifteenMinHighProximity = (currentPrice / recentFifteenMinHigh) * 100;
            decimal hourlyHighProximity = (currentPrice / recentHourlyHigh) * 100;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Current price is at:");
            Console.WriteLine($"  • {oneMinHighProximity:F2}% of 8-min high");
            Console.WriteLine($"  • {fiveMinHighProximity:F2}% of 30-min high");
            Console.WriteLine($"  • {fifteenMinHighProximity:F2}% of 1-hour high");
            Console.WriteLine($"  • {hourlyHighProximity:F2}% of 4-hour high");

            // If price is very close to multiple timeframe highs, it's likely overextended
            // MODIFIED: Made criteria slightly more lenient
            if (oneMinHighProximity > 99m && fiveMinHighProximity > 98.5m && fifteenMinHighProximity > 98m)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Price appears to be at a local peak across multiple timeframes");
                return (false, 0, "Price is at a local peak - avoid buying at resistance");
            }

            // --- DETECT POTENTIAL PULLBACKS FOR ENTRY ---

            // Calculate recent pullbacks from highs in different timeframes
            var oneMinPullbacks = new List<decimal>();
            var fiveMinPullbacks = new List<decimal>();
            var fifteenMinPullbacks = new List<decimal>();

            // Find pullbacks in 1-minute timeframe (short-term)
            for (int i = 2; i < 30; i++)
            {
                if (i >= oneMinCandles.Data.Count()) break;

                var candle = oneMinCandles.Data.ToList()[i];
                var prevCandle1 = oneMinCandles.Data.ToList()[i - 1];
                var prevCandle2 = oneMinCandles.Data.ToList()[i - 2];

                // Pattern: High -> Lower High -> Pullback
                if (prevCandle2.HighPrice > prevCandle1.HighPrice &&
                    prevCandle1.LowPrice > candle.LowPrice)
                {
                    decimal pullbackDepth = (prevCandle2.HighPrice - candle.LowPrice) / prevCandle2.HighPrice * 100;
                    oneMinPullbacks.Add(pullbackDepth);
                }
            }

            // Find pullbacks in 5-minute timeframe (medium-term)
            for (int i = 2; i < 15; i++)
            {
                if (i >= fiveMinCandles.Data.Count()) break;

                var candle = fiveMinCandles.Data.ToList()[i];
                var prevCandle1 = fiveMinCandles.Data.ToList()[i - 1];
                var prevCandle2 = fiveMinCandles.Data.ToList()[i - 2];

                if (prevCandle2.HighPrice > prevCandle1.HighPrice &&
                    prevCandle1.LowPrice > candle.LowPrice)
                {
                    decimal pullbackDepth = (prevCandle2.HighPrice - candle.LowPrice) / prevCandle2.HighPrice * 100;
                    fiveMinPullbacks.Add(pullbackDepth);
                }
            }

            // Find pullbacks in 15-minute timeframe (longer-term)
            for (int i = 2; i < 12; i++)
            {
                if (i >= fifteenMinCandles.Data.Count()) break;

                var candle = fifteenMinCandles.Data.ToList()[i];
                var prevCandle1 = fifteenMinCandles.Data.ToList()[i - 1];
                var prevCandle2 = fifteenMinCandles.Data.ToList()[i - 2];

                if (prevCandle2.HighPrice > prevCandle1.HighPrice &&
                    prevCandle1.LowPrice > candle.LowPrice)
                {
                    decimal pullbackDepth = (prevCandle2.HighPrice - candle.LowPrice) / prevCandle2.HighPrice * 100;
                    fifteenMinPullbacks.Add(pullbackDepth);
                }
            }

            // Calculate average pullback depth for each timeframe
            decimal avgOneMinPullback = oneMinPullbacks.Count > 0 ? oneMinPullbacks.Average() : 0.5m;
            decimal avgFiveMinPullback = fiveMinPullbacks.Count > 0 ? fiveMinPullbacks.Average() : 0.8m;
            decimal avgFifteenMinPullback = fifteenMinPullbacks.Count > 0 ? fifteenMinPullbacks.Average() : 1.2m;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Average pullback depths:");
            Console.WriteLine($"  • 1min: {avgOneMinPullback:F2}%");
            Console.WriteLine($"  • 5min: {avgFiveMinPullback:F2}%");
            Console.WriteLine($"  • 15min: {avgFifteenMinPullback:F2}%");

            // --- DETECT TREND DIRECTION ACROSS TIMEFRAMES ---

            // Calculate EMAs for trend determination
            var ema20_1h = CalculateEMA(hourlyCandles.Data.Select(c => c.ClosePrice).ToList(), 20);
            var ema50_1h = CalculateEMA(hourlyCandles.Data.Select(c => c.ClosePrice).ToList(), 50);
            var ema9_15m = CalculateEMA(fifteenMinCandles.Data.Select(c => c.ClosePrice).ToList(), 9);
            var ema21_15m = CalculateEMA(fifteenMinCandles.Data.Select(c => c.ClosePrice).ToList(), 21);

            // Check if price is below key EMAs (potential value entry)
            bool belowEMA20_1h = currentPrice < ema20_1h;
            bool belowEMA50_1h = currentPrice < ema50_1h;
            bool belowEMA9_15m = currentPrice < ema9_15m;
            bool belowEMA21_15m = currentPrice < ema21_15m;

            TrendDirection shortTrend = DetermineTrend(fifteenMinCandles.Data.Take(4).ToList());
            TrendDirection mediumTrend = DetermineTrend(hourlyCandles.Data.Take(6).ToList());
            TrendDirection longTrend = DetermineTrend(fourHourCandles.Data.Take(6).ToList());

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trend analysis:");
            Console.WriteLine($"  • Short-term (15m): {shortTrend}");
            Console.WriteLine($"  • Medium-term (1h): {mediumTrend}");
            Console.WriteLine($"  • Long-term (4h): {longTrend}");

            // --- VOLUME ANALYSIS ---

            // Check for unusual volume spikes (indicating potential significant movement)
            decimal avgVolume_15m = fifteenMinCandles.Data.Skip(1).Take(12).Average(c => c.Volume);
            decimal latestVolume_15m = fifteenMinCandles.Data.First().Volume;
            decimal volumeRatio_15m = latestVolume_15m / avgVolume_15m;

            decimal avgVolume_1h = hourlyCandles.Data.Skip(1).Take(12).Average(c => c.Volume);
            decimal latestVolume_1h = hourlyCandles.Data.First().Volume;
            decimal volumeRatio_1h = latestVolume_1h / avgVolume_1h;

            bool unusualVolume = volumeRatio_15m > 2.0m || volumeRatio_1h > 2.0m;
            bool increasingVolume = IsVolumeIncreasing(fifteenMinCandles.Data.Take(6).ToList());

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Volume analysis:");
            Console.WriteLine($"  • 15min volume ratio: {volumeRatio_15m:F2}x average");
            Console.WriteLine($"  • 1h volume ratio: {volumeRatio_1h:F2}x average");
            Console.WriteLine($"  • Increasing volume pattern: {increasingVolume}");

            // --- IDENTIFY RECENT BOTTOM FORMATIONS ---

            // Look for double bottom, triple bottom, or reversal patterns in recent candles
            bool hasBottomFormation = DetectBottomFormation(fifteenMinCandles.Data.Take(12).ToList());
            bool hasReversal = DetectReversal(fifteenMinCandles.Data.Take(6).ToList());

            if (hasBottomFormation || hasReversal)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Detected potential bottom formation or reversal pattern");
            }

            // --- CALCULATE IDEAL ENTRY PRICE BASED ON FINDINGS ---

            decimal idealEntryPrice = 0;
            bool strongBuySignal = false;
            string entryReason = "";

            // Check if the price near a strong resistance or support
            bool nearResistance = priceLevels
                .Where(p => p.Type == "Resistance" && p.Strength >= 2 &&
                          p.Price > currentPrice && (p.Price - currentPrice) / currentPrice < 0.03m) // Within 3%
                .Any();

            // Case 1: Strong uptrend with fresh momentum - likely rally
            if ((shortTrend == TrendDirection.Up && mediumTrend == TrendDirection.Up) &&
                (unusualVolume || increasingVolume) && currentPrice > ema9_15m)
            {
                // Fresh momentum detected, ride the wave
                // BUT still try to get a small discount from current price
                idealEntryPrice = currentPrice * 0.997m; // 0.3% discount
                strongBuySignal = true;
                entryReason = "Fresh momentum detected with increasing volume - possible rally";
            }
            // Case 2: Pullback in overall positive trend - good value entry
            else if ((mediumTrend == TrendDirection.Up || longTrend == TrendDirection.Up) &&
                    (belowEMA9_15m || (shortTrend == TrendDirection.Down && currentPrice < recentFifteenMinHigh * 0.985m)))
            {
                // Pullback in uptrend - good entry opportunity
                // Use average pullback depth to calculate ideal entry
                decimal targetPullback = Math.Min(avgFiveMinPullback, 1.5m); // Cap at 1.5%

                // Calculate target price based on recent high and expected pullback
                idealEntryPrice = recentFifteenMinHigh * (1 - targetPullback / 100);

                // If current price is already below our target, use current price
                if (currentPrice < idealEntryPrice)
                {
                    idealEntryPrice = currentPrice;
                }

                strongBuySignal = true;
                entryReason = "Pullback in overall uptrend - value entry";
            }
            // Case 3: Reversal after downtrend with supporting evidence
            else if ((shortTrend == TrendDirection.Up && mediumTrend == TrendDirection.Down) &&
                    (hasBottomFormation || hasReversal) && increasingVolume)
            {
                // Potential reversal from downtrend
                // Look for price breaking above short-term EMAs but still below medium-term ones
                if (currentPrice > ema9_15m && belowEMA20_1h)
                {
                    // Target a small discount from current price
                    idealEntryPrice = currentPrice * 0.996m;
                    strongBuySignal = true;
                    entryReason = "Potential reversal from downtrend with supporting pattern";
                }
            }
            // Case 4: Value-based entry near support level
            else
            {
                // Check if the price near a strong support
                var nearbySupport = priceLevels
                    .Where(p => p.Type == "Support" && p.Strength >= 3 &&
                          p.Price < currentPrice && (currentPrice - p.Price) / currentPrice < 0.03m) // Within 3%
                    .OrderByDescending(p => p.Price)
                    .FirstOrDefault();

                if (nearbySupport != null)
                {
                    // Ideal entry is slightly above support level
                    idealEntryPrice = nearbySupport.Price * 1.003m;

                    // If current price is already below our ideal entry, use current price
                    if (currentPrice < idealEntryPrice)
                    {
                        idealEntryPrice = currentPrice;
                    }

                    strongBuySignal = true;
                    entryReason = $"Price approaching strong support at {nearbySupport.Price}";
                }
                else
                {
                    // Default case - calculate based on EMA and recent lows
                    var recentLows = fifteenMinCandles.Data.Take(8).Select(c => c.LowPrice).ToList();
                    decimal avgLow = recentLows.Average();

                    if (currentPrice < avgLow * 1.01m) // Current price is within 1% of recent average low
                    {
                        idealEntryPrice = currentPrice;
                        strongBuySignal = true;
                        entryReason = "Price at favorable value level compared to recent lows";
                    }
                    else
                    {
                        // Target a more significant pullback since we're not near obvious support
                        decimal targetPrice = avgLow * 1.005m; // Slightly above average low
                        if (targetPrice < currentPrice * 0.985m) // If target is more than 1.5% down
                        {
                            // Adjust to reasonable amount below current price
                            targetPrice = currentPrice * 0.985m;
                        }

                        idealEntryPrice = targetPrice;
                        strongBuySignal = false;
                        entryReason = "Waiting for better entry price";
                    }
                }
            }

            // Special case: When price is at very strong support and showing signs of turning up
            if (!strongBuySignal) // Only check if we haven't found a signal yet
            {
                var veryStrongSupport = priceLevels
                    .Where(p => p.Type == "Support" && p.Strength >= 3 &&
                              Math.Abs((p.Price - currentPrice) / currentPrice) < 0.01m) // Within 1%
                    .OrderByDescending(p => p.Strength)
                    .FirstOrDefault();

                if (veryStrongSupport != null && !nearResistance && currentPrice < ema9_15m * 1.005m)
                {
                    idealEntryPrice = currentPrice * 0.998m;
                    strongBuySignal = true;
                    entryReason = $"Price at very strong support level (Strength: {veryStrongSupport.Strength}) with signs of upward movement";
                }
            }

            // Round entry price to proper precision
            idealEntryPrice = Math.Round(idealEntryPrice, 8);

            // Final entry decision
            bool shouldEnter = strongBuySignal;

            if (shouldEnter)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Entry opportunity identified: {entryReason}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ideal entry price: {idealEntryPrice} (current: {currentPrice})");
            }
            else
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] No compelling entry opportunity detected: {entryReason}");
            }

            return (shouldEnter, idealEntryPrice, entryReason);
        }

        // Helper functions for trend analysis
        private static TrendDirection DetermineTrend(List<IBinanceKline> candles)
        {
            if (candles.Count() < 3)
                return TrendDirection.Sideways;

            // Calculate price movement
            decimal firstPrice = candles.Last().ClosePrice;
            decimal lastPrice = candles.First().ClosePrice;
            decimal priceChange = (lastPrice - firstPrice) / firstPrice * 100;

            // Count bullish vs bearish candles
            int bullishCount = candles.Count(c => c.ClosePrice > c.OpenPrice);
            int bearishCount = candles.Count(c => c.ClosePrice < c.OpenPrice);

            // Check for higher highs and higher lows (uptrend)
            bool higherHighs = true;
            bool higherLows = true;
            var candlesList = candles.ToList();

            for (int i = 0; i < candles.Count() - 1; i++)
            {
                if (candlesList[i].HighPrice <= candlesList[i + 1].HighPrice)
                    higherHighs = false;

                if (candlesList[i].LowPrice <= candlesList[i + 1].LowPrice)
                    higherLows = false;
            }

            // Check for lower highs and lower lows (downtrend)
            bool lowerHighs = true;
            bool lowerLows = true;

            for (int i = 0; i < candles.Count() - 1; i++)
            {
                if (candlesList[i].HighPrice >= candlesList[i + 1].HighPrice)
                    lowerHighs = false;

                if (candlesList[i].LowPrice >= candlesList[i + 1].LowPrice)
                    lowerLows = false;
            }

            // Determine trend based on collected data
            if ((priceChange > 1.5m && bullishCount > bearishCount) || (higherHighs && higherLows))
            {
                return TrendDirection.Up;
            }
            else if ((priceChange < -1.5m && bearishCount > bullishCount) || (lowerHighs && lowerLows))
            {
                return TrendDirection.Down;
            }
            else
            {
                return TrendDirection.Sideways;
            }
        }

        private static bool IsVolumeIncreasing(List<IBinanceKline> candles)
        {
            if (candles.Count() < 3)
                return false;

            var candlesList = candles.ToList();

            // Check if volume is increasing over consecutive candles
            int increasingCount = 0;

            for (int i = 0; i < candles.Count() - 1; i++)
            {
                if (candlesList[i].Volume > candlesList[i + 1].Volume)
                    increasingCount++;
            }

            // Consider volume increasing if majority of recent candles show increasing volume
            return (double)increasingCount / (candles.Count() - 1) >= 0.6;
        }
        private static bool DetectBottomFormation(List<IBinanceKline> candles)
        {
            if (candles.Count() < 6)
                return false;

            // Look for double-bottom pattern (W shape)
            var lows = candles.Select(c => c.LowPrice).ToList();

            // MODIFIED: More lenient double-bottom pattern detection
            // Crude double-bottom pattern: two similar lows with a higher low in between
            for (int i = 2; i < lows.Count - 2; i++)
            {
                decimal low1 = lows[i];
                decimal low2 = lows[i - 2];
                decimal middleLow = lows[i - 1];

                // Check if the two lows are within 0.8% of each other (was 0.5%)
                if (Math.Abs(low1 - low2) / low1 < 0.008m &&
                    middleLow > low1 * 1.003m && // Middle low is at least 0.3% higher (was 0.5%)
                    middleLow > low2 * 1.003m)
                {
                    return true;
                }
            }

            // Look for triple bottom (similar logic to double bottom)
            for (int i = 4; i < lows.Count - 2; i++)
            {
                decimal low1 = lows[i];
                decimal low2 = lows[i - 2];
                decimal low3 = lows[i - 4];

                // Check if the three lows are within 0.8% of each other
                if (Math.Abs(low1 - low2) / low1 < 0.008m &&
                    Math.Abs(low2 - low3) / low2 < 0.008m &&
                    Math.Abs(low1 - low3) / low1 < 0.008m)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DetectReversal(List<IBinanceKline> candles)
        {
            if (candles.Count() < 4)
                return false;

            // Look for reversal patterns like bullish engulfing
            for (int i = 1; i < candles.Count(); i++)
            {
                // Current and previous candles
                var current = candles.ToList()[i];
                var previous = candles.ToList()[i - 1];

                // Bullish engulfing pattern
                if (previous.ClosePrice < previous.OpenPrice && // Previous candle is bearish
                    current.ClosePrice > current.OpenPrice && // Current candle is bullish
                    current.OpenPrice < previous.ClosePrice && // Current opens below previous close
                    current.ClosePrice > previous.OpenPrice) // Current closes above previous open
                {
                    return true;
                }

                // Hammer pattern (potential reversal in downtrend)
                decimal bodySize = Math.Abs(current.ClosePrice - current.OpenPrice);
                decimal lowerWick = Math.Min(current.OpenPrice, current.ClosePrice) - current.LowPrice;
                decimal upperWick = current.HighPrice - Math.Max(current.OpenPrice, current.ClosePrice);

                if (lowerWick > bodySize * 2 && upperWick < bodySize * 0.5m &&
                    current.ClosePrice > current.OpenPrice) // Bullish candle with long lower wick
                {
                    return true;
                }
            }

            return false;
        }

        // Calculate EMA
        private static decimal CalculateEMA(List<decimal> prices, int period)
        {
            if (prices.Count < period)
                return prices.Average();

            decimal smoothingFactor = 2m / (period + 1m);
            decimal ema = prices.Take(period).Average();

            for (int i = period; i < prices.Count; i++)
            {
                ema = (prices[i] * smoothingFactor) + (ema * (1 - smoothingFactor));
            }

            return ema;
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
                        // In strong uptrend, buy at pullback rather than chasing highs
                        decimal currentPrice = fifteenMinCandles[0].ClosePrice;
                        decimal recentlowPrice = fifteenMinCandles.Take(8).Min(c => c.LowPrice);

                        // Calculate entry price between current and recent low
                        decimal entryRange = currentPrice - recentlowPrice;
                        idealEntryPrice = currentPrice - (entryRange * 0.3m); // Target 30% into the pullback

                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Strong 4h uptrend - targeting entry at {idealEntryPrice} on pullback");
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
                            idealEntryPrice = currentPrice * 0.997m; // Small discount to current
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] End of 4h period in sideways market - targeting entry at {idealEntryPrice}");
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
                            // In 1h uptrend, target sensible entry point
                            decimal currentPrice = fifteenMinCandles[0].ClosePrice;
                            decimal recentLow = fifteenMinCandles.Take(8).Min(k => k.LowPrice);

                            // Target a reasonable entry between current price and recent low
                            idealEntryPrice = currentPrice - ((currentPrice - recentLow) * 0.3m);
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 1h uptrend - targeting entry at {idealEntryPrice}");
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
                            // In short-term uptrend, target modest pullback
                            decimal currentPrice = fifteenMinCandles[0].ClosePrice;
                            idealEntryPrice = currentPrice * 0.997m;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 15min uptrend - targeting small pullback entry at {idealEntryPrice}");
                        }
                        else if (trendAnalysis.Direction == MarketDirection.Downtrend)
                        {
                            // In short-term downtrend, wait for smallest sign of reversal
                            if (near15mPeriodEnd || trendAnalysis.PotentialReversal)
                            {
                                idealEntryPrice = fifteenMinCandles[0].ClosePrice * 0.998m;
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
                            idealEntryPrice = currentPrice * 0.998m;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sideways 15min market - targeting modest discount to current price");
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
            TrendAnalysis Analyze4HourTrend(List<IBinanceKline> candles, bool nearPeriodEnd, string symbolName)
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
            TrendAnalysis Analyze1HourTrend(List<IBinanceKline> candles, bool nearPeriodEnd, string symbolName)
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
            TrendAnalysis Analyze15MinTrend(List<IBinanceKline> candles, bool nearPeriodEnd, string symbolName)
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

            if (nearStrongSupport && marketCondition.RSIVolatility < 65)
            {
                return true;
            }

            // If momentum is good, enter
            if (marketCondition.RSIVolatility >= 30 && marketCondition.RSIVolatility <= 60 && marketCondition.TrendDirection != TrendDirection.Down)
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
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{symbol}]=> Limit order placed successfully (ID: {result.Data.Id}), waiting for fill (timeout: 10s)");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{symbol}]=> Limit order FAILED: {result.Error?.Message}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]  [{symbol}]=> Exception placing limit order: {ex.Message}");

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

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{symbol}]=> Limit order timeout reached, cancelling order");
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