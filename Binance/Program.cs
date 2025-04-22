using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using TradingBot.Models;
using TradingBot.Analyzers;
using TradingBot.Strategies;
using TradingBot.Utilities;
using TradingBot;
using Newtonsoft.Json.Linq;

class Program
{
    // Performance tracking to optimize parameters
    private static Dictionary<string, StrategyMetrics> strategyPerformance = new Dictionary<string, StrategyMetrics>();
    private static readonly string performanceFilePath = "strategy_performance.json";

    // Arbitrage tracking
    private static readonly string arbitragePerformanceFilePath = "arbitrage_performance.json";
    private static List<ArbitrageResult> arbitrageHistory = new List<ArbitrageResult>();
    private static decimal totalArbitrageProfit = 0;
    private static int arbitrageAttempts = 0;
    private static int successfulArbitrageTrades = 0;

    // Multi-coin trading parameters
    private static int maxConcurrentCoins = 3; // Set to 3 as requested
    private static Dictionary<string, bool> activePositions = new Dictionary<string, bool>();
    private static Dictionary<string, DateTime> lastTradeAttemptTime = new Dictionary<string, DateTime>();
    private static object tradeLock = new object(); // For thread safety when managing multiple coins
    private static List<string> activeCoins = new List<string>();
    private static decimal minimumTradeSize = 6; // Set to 12 as requested
    private static List<string> failedCoins = new List<string>(); // To track coins that failed due to fundamental issues

    // Track API restricted symbols
    private static HashSet<string> restrictedSymbols = new HashSet<string>();

    // Added to track current balance more accurately
    private static decimal currentBalance = 0;
    private static DateTime lastBalanceCheck = DateTime.MinValue;

    // Arbitrage system
    private static TriangularArbitrageSystem arbitrageSystem;
    private static DateTime lastArbitrageCheck = DateTime.MinValue;
    private static TimeSpan arbitrageCheckInterval = TimeSpan.FromMinutes(5); // Check for arbitrage every 5 minutes
    private static decimal arbitrageBudgetAllocation = 0.3m; // Use 30% of available funds for arbitrage
    private static bool arbitrageSystemInitialized = false;
    private static bool isArbitrageRunning = false;

    // Cooldown for arbitrage after a failed attempt
    private static DateTime arbitrageCooldownUntil = DateTime.MinValue;
    private static readonly TimeSpan arbitrageCooldownTime = TimeSpan.FromMinutes(10);

    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting enhanced Binance scalping bot...");

        // Parse command line arguments for optional settings
        if (args.Length > 0)
        {
            // Check for maximum coins setting
            int maxCoinsArgIndex = Array.IndexOf(args, "--max-coins");
            if (maxCoinsArgIndex >= 0 && args.Length > maxCoinsArgIndex + 1)
            {
                if (int.TryParse(args[maxCoinsArgIndex + 1], out int maxCoinsValue))
                {
                    maxConcurrentCoins = maxCoinsValue;
                    Console.WriteLine($"Setting maximum concurrent coins to: {maxConcurrentCoins}");
                }
            }

            // Check for minimum trade size setting
            int minSizeArgIndex = Array.IndexOf(args, "--min-trade");
            if (minSizeArgIndex >= 0 && args.Length > minSizeArgIndex + 1)
            {
                if (decimal.TryParse(args[minSizeArgIndex + 1], out decimal minTradeValue))
                {
                    minimumTradeSize = minTradeValue;
                    Console.WriteLine($"Setting minimum trade size to: ${minimumTradeSize}");
                }
            }

            // Check for arbitrage budget allocation setting
            int arbitrageAllocArgIndex = Array.IndexOf(args, "--arb-allocation");
            if (arbitrageAllocArgIndex >= 0 && args.Length > arbitrageAllocArgIndex + 1)
            {
                if (decimal.TryParse(args[arbitrageAllocArgIndex + 1], out decimal arbAllocation) && arbAllocation > 0 && arbAllocation < 1)
                {
                    arbitrageBudgetAllocation = arbAllocation;
                    Console.WriteLine($"Setting arbitrage allocation to: {arbitrageBudgetAllocation:P0} of available balance");
                }
            }
        }

        // Load previous performance data if available
        LoadPerformanceData();
        LoadArbitrageHistory();

        // Load previously restricted symbols if available
        LoadRestrictedSymbols();

        string APIKEY = "FEzhIljFEQ44MkLplbdcHUGQ55imdHkYT9TpxEgI6iUHMe7fMGnAL8TPiMCUIF7A";
        string APISECRET = "ZQzfY6ogJjpms3dXcOmBGpx269N4hhUmxhN48leofp6lHRXXNchyB3dj8m1KXmkQ";

        var client = new BinanceRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(APIKEY, APISECRET);
        });

        // Initialize arbitrage system
        arbitrageSystem = new TriangularArbitrageSystem(client);

        // Get account balance info at start
        var accountInfo = await client.SpotApi.Account.GetAccountInfoAsync();
        if (accountInfo.Success)
        {
            Console.WriteLine("Account Balance Information:");
            var usdtBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
            if (usdtBalance != null)
            {
                Console.WriteLine($"USDT Balance: {usdtBalance.Available} (Free) + {usdtBalance.Locked} (Locked)");
                currentBalance = usdtBalance.Available - 10; // Use actual USDT balance
                lastBalanceCheck = DateTime.Now;
            }
        }

        // Configuration parameters - FIXED calculation
        decimal initialBudget = currentBalance;
        decimal budget = initialBudget;
        decimal maxDailyLoss = initialBudget * 0.15m; // Max 5% daily loss
        decimal maxDailyProfit = initialBudget * 0.95m; // Target 15% daily profit
        int maxConsecutiveLosses = 3; // Stop after 3 consecutive losses

        Console.WriteLine($"Initial budget: ${initialBudget:F2}");
        Console.WriteLine($"Daily loss limit: ${maxDailyLoss:F2} (5% of initial balance)");

        DateTime tradingStartTime = DateTime.Now;
        int consecutiveLosses = 0;
        int totalTrades = 0;
        int profitableTrades = 0;
        decimal totalProfit = 0;

        // Market selection parameters
        int coinRotationHours = 2; // Re-evaluate best coin more frequently
        DateTime lastCoinSelection = DateTime.MinValue;

        // Blacklist for coins that have had recent losses
        Dictionary<string, DateTime> tradingBlacklist = new Dictionary<string, DateTime>();

        // Daily stats reset
        DateTime dailyStatsReset = DateTime.Now.Date.AddDays(1);

        while (true)
        {
            try
            {
                // Reset daily stats if needed
                if (DateTime.Now >= dailyStatsReset)
                {
                    Console.WriteLine("Resetting daily statistics");
                    consecutiveLosses = 0;
                    dailyStatsReset = DateTime.Now.Date.AddDays(1);

                    // Clear any blacklisted coins
                    tradingBlacklist.Clear();

                    // Reset daily tracking
                    initialBudget = currentBalance;
                    totalProfit = 0;
                    maxDailyLoss = initialBudget * 0.05m;
                    maxDailyProfit = initialBudget * 0.15m;

                    Console.WriteLine($"New daily budget: ${initialBudget:F2}");
                    Console.WriteLine($"New daily loss limit: ${maxDailyLoss:F2}");

                    // Reset arbitrage stats for the day
                    totalArbitrageProfit = 0;
                    arbitrageAttempts = 0;
                    successfulArbitrageTrades = 0;
                }

                // Check balance periodically (every 15 minutes)
                if (DateTime.Now > lastBalanceCheck.AddMinutes(15))
                {
                    var balanceCheck = await client.SpotApi.Account.GetAccountInfoAsync();
                    if (balanceCheck.Success)
                    {
                        var usdtBalance = balanceCheck.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
                        if (usdtBalance != null)
                        {
                            currentBalance = usdtBalance.Available;
                            lastBalanceCheck = DateTime.Now;
                            Console.WriteLine($"Updated USDT Balance: {currentBalance:F8} (Free)");
                        }
                    }
                }

                // Clean up expired blacklist entries
                var expiredBlacklists = tradingBlacklist
                    .Where(pair => DateTime.Now.Subtract(pair.Value).TotalMinutes > 60)
                    .Select(pair => pair.Key).ToList();

                foreach (var symbol in expiredBlacklists)
                {
                    tradingBlacklist.Remove(symbol);
                    Console.WriteLine($"Removed {symbol} from trading blacklist");
                }

                // Update max concurrent coins based on available budget
                // Take arbitrage allocation into account when calculating effective budget for scalping
                decimal effectiveScalpingBalance = currentBalance * (1 - arbitrageBudgetAllocation);
                int effectiveMaxCoins = Math.Min(maxConcurrentCoins, (int)(effectiveScalpingBalance / minimumTradeSize));
                if (effectiveMaxCoins < 1) effectiveMaxCoins = 1;

                // --- START ARBITRAGE SYSTEM CHECK ---
                // Check for arbitrage opportunities periodically and when no active positions
                if (false && !isArbitrageRunning &&
                    DateTime.Now > arbitrageCooldownUntil &&
                    DateTime.Now > lastArbitrageCheck.AddMinutes(arbitrageCheckInterval.TotalMinutes) &&
                    currentBalance >= 10) // Minimum $50 USDT for arbitrage
                {
                    decimal arbitrageBudget = 20;// currentBalance * arbitrageBudgetAllocation;

                    // Initialize arbitrage system if not already done
                    if (!arbitrageSystemInitialized)
                    {
                        try
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Initializing arbitrage system...");
                            await arbitrageSystem.Initialize();
                            arbitrageSystemInitialized = true;
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Arbitrage system initialized successfully");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error initializing arbitrage system: {ex.Message}");
                            arbitrageCooldownUntil = DateTime.Now.Add(TimeSpan.FromMinutes(30)); // Try again in 30 minutes
                        }
                    }

                    if (arbitrageSystemInitialized)
                    {
                        try
                        {
                            isArbitrageRunning = true;
                            arbitrageAttempts++;

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Checking for arbitrage opportunities with budget: ${arbitrageBudget:F2} USDT...");

                            // Check and execute arbitrage if profitable
                            var arbitrageResult = await arbitrageSystem.CheckAndExecuteArbitrage(arbitrageBudget);

                            lastArbitrageCheck = DateTime.Now;

                            if (arbitrageResult.Success)
                            {
                                // Record successful arbitrage
                                totalArbitrageProfit += arbitrageResult.Profit;
                                successfulArbitrageTrades++;
                                arbitrageHistory.Add(arbitrageResult);

                                // Log the successful arbitrage
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Arbitrage profit: {arbitrageResult.Profit:F8} USDT ({arbitrageResult.ProfitPercent:F2}%)");
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Path: {arbitrageResult.Path}");

                                // Save arbitrage history
                                SaveArbitrageHistory();

                                // Update actual balance after arbitrage
                                lastBalanceCheck = DateTime.MinValue; // Force balance check on next loop
                            }
                            else if (!string.IsNullOrEmpty(arbitrageResult.Message) &&
                                     !arbitrageResult.Message.StartsWith("No profitable arbitrage"))
                            {
                                // Log if there was an actual error (not just "no opportunity")
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Arbitrage error: {arbitrageResult.Message}");

                                // Set a cooldown if we had an execution error
                                if (arbitrageResult.Message.Contains("failed") ||
                                    arbitrageResult.Message.Contains("error") ||
                                    arbitrageResult.Message.Contains("Exception"))
                                {
                                    arbitrageCooldownUntil = DateTime.Now.Add(arbitrageCooldownTime);
                                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Arbitrage cooldown until {arbitrageCooldownUntil:HH:mm:ss} due to error");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] No profitable arbitrage opportunities found");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error during arbitrage check: {ex.Message}");
                            arbitrageCooldownUntil = DateTime.Now.Add(arbitrageCooldownTime); // Cooldown after error
                        }
                        finally
                        {
                            isArbitrageRunning = false;
                        }
                    }
                }
                // --- END ARBITRAGE SYSTEM CHECK ---

                // Check if we need to update our list of monitored coins
                if (DateTime.Now > lastCoinSelection.AddHours(coinRotationHours) || activeCoins.Count == 0)
                {
                    Console.WriteLine("Selecting best coins for trading...");
                    var topCoins = await CoinSelector.SelectBestTradingPairs(client, strategyPerformance, effectiveMaxCoins * 8);

                    // Filter out restricted symbols, blacklisted coins, and recent attempts
                    topCoins = topCoins
                        .Where(c => !restrictedSymbols.Contains(c))
                        .Where(c => !tradingBlacklist.ContainsKey(c))
                        .Where(c => !lastTradeAttemptTime.ContainsKey(c) ||
                               DateTime.Now.Subtract(lastTradeAttemptTime[c]).TotalMinutes > 15)
                        .ToList();

                    Console.WriteLine("Selected top coins after filtering:");
                    foreach (var coin in topCoins.Take(6))
                    {
                        Console.WriteLine($"- {coin}");
                    }

                    // Update active coins list - keep existing active positions
                    activeCoins = topCoins.Take(effectiveMaxCoins).ToList();

                    // Add currently active positions to make sure we keep monitoring them
                    foreach (var position in activePositions.Where(p => p.Value))
                    {
                        if (!activeCoins.Contains(position.Key) && activeCoins.Count < effectiveMaxCoins)
                        {
                            activeCoins.Add(position.Key);
                        }
                    }

                    lastCoinSelection = DateTime.Now;
                    Console.WriteLine($"Selected {activeCoins.Count} coins for monitoring: {string.Join(", ", activeCoins)}");
                }

                // Check global market conditions (like BTC trend)
                var btcMarketCondition = await MarketAnalyzer.AnalyzeMarketCondition(client, "BTCUSDT");
                bool isGlobalMarketFavorable = btcMarketCondition.IsFavorable ||
                                              btcMarketCondition.TrendDirection != TrendDirection.Down;

                if (!isGlobalMarketFavorable)
                {
                    Console.WriteLine("Caution: Global market conditions unfavorable. Adjusting risk parameters.");
                    // Reduce position sizing in unfavorable global markets
                    budget = Math.Min(effectiveScalpingBalance, initialBudget * 0.6m); // More conservative
                }
                else
                {
                    budget = effectiveScalpingBalance; // Use available scalping balance in favorable conditions
                }

                // Calculate per-coin budget based on active positions and max concurrent coins
                int activePositionCount = activePositions.Count(p => p.Value);
                int remainingSlots = Math.Max(1, effectiveMaxCoins - activePositionCount);
                decimal perCoinBudget = Math.Max(minimumTradeSize, budget / (remainingSlots * 1.1m)); // Add 10% buffer

                // Process each active coin with a delay between them to prevent API rate limit issues
                foreach (var symbol in activeCoins.ToList()) // Use ToList() to avoid collection modified exception
                {
                    // Skip restricted symbols
                    if (restrictedSymbols.Contains(symbol))
                    {
                        Console.WriteLine($"Skipping {symbol} - API restricted");
                        continue;
                    }

                    // Check if this coin is on the blacklist
                    if (tradingBlacklist.ContainsKey(symbol))
                    {
                        Console.WriteLine($"Skipping {symbol} - on trading blacklist");
                        continue;
                    }

                    // Verify position status before deciding to skip
                    bool isReallyActive = false;
                    lock (tradeLock)
                    {
                        activePositions.TryGetValue(symbol, out isReallyActive);
                    }

                    // Skip if we already have an active position for this coin
                    if (isReallyActive)
                    {
                        Console.WriteLine($"Skipping {symbol} - already in active position");
                        continue;
                    }

                    Console.WriteLine($"\nAnalyzing {symbol}...");

                    // Get symbol info
                    var exchangeInfo = await client.SpotApi.ExchangeData.GetExchangeInfoAsync();
                    var symbolInfo = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                    if (symbolInfo == null)
                    {
                        Console.WriteLine($"Failed to retrieve symbol info for {symbol}. Skipping.");
                        continue;
                    }

                    // Get lot size filter and price filter
                    var lotSizeFilter = symbolInfo.LotSizeFilter;
                    decimal stepSize = lotSizeFilter.StepSize;
                    decimal minQuantity = lotSizeFilter.MinQuantity;
                    var priceFilter = symbolInfo.PriceFilter;
                    decimal tickSize = priceFilter.TickSize;

                    // Get current price to check if minimum quantity is affordable
                    var ticker = await client.SpotApi.ExchangeData.GetTickerAsync(symbol);
                    if (!ticker.Success)
                    {
                        Console.WriteLine($"Failed to get current price for {symbol}. Skipping.");
                        continue;
                    }
                    decimal currentPrice = ticker.Data.LastPrice;

                    // Check if minimum trade size is viable
                    if (currentPrice * minQuantity > perCoinBudget * 0.85m)
                    {
                        Console.WriteLine($"Skipping {symbol} - minimum quantity ({minQuantity}) exceeds budget allocation");
                        continue;
                    }

                    // Get trade fees
                    var tradeFeeResult = await client.SpotApi.Account.GetTradeFeeAsync(symbol);
                    decimal takerFee = tradeFeeResult.Success ?
                        tradeFeeResult.Data.FirstOrDefault()?.TakerFee ?? 0.001m : 0.001m;
                    decimal makerFee = tradeFeeResult.Success ?
                        tradeFeeResult.Data.FirstOrDefault()?.MakerFee ?? 0.001m : 0.001m;

                    Console.WriteLine($"Trading fees for {symbol} - Maker: {makerFee:P4}, Taker: {takerFee:P4}");

                    // Check specific coin market conditions
                    var marketCondition = await MarketAnalyzer.AnalyzeMarketCondition(client, symbol);

                    // Optimize parameters based on historical performance
                    marketCondition = ParameterOptimizer.OptimizeParameters(marketCondition, symbol, strategyPerformance);

                    if (!marketCondition.IsFavorable)
                    {
                        Console.WriteLine($"Market conditions unfavorable for {symbol}. Reason: {marketCondition.Reason}");

                        // If the reason is low volatility or something fundamental about the coin, mark for replacement
                        if (marketCondition.Reason.Contains("volatility") ||
                            marketCondition.Reason.Contains("not suitable") ||
                            marketCondition.Reason.Contains("waiting for better entry"))
                        {
                            lock (tradeLock)
                            {
                                if (!failedCoins.Contains(symbol))
                                {
                                    failedCoins.Add(symbol);
                                    Console.WriteLine($"Marking {symbol} for replacement due to: {marketCondition.Reason}");
                                }
                            }
                        }

                        // Update last attempt time to prevent immediate rechecking
                        lastTradeAttemptTime[symbol] = DateTime.Now;
                        continue;
                    }

                    // Check for relevant price levels
                    var priceLevels = await PriceLevelAnalyzer.IdentifyKeyPriceLevels(client, symbol);
                    Console.WriteLine($"Identified Key Price Levels for {symbol}:");
                    foreach (var level in priceLevels.Take(3))
                    {
                        Console.WriteLine($"- {level.Type}: {level.Price} (Strength: {level.Strength:F2})");
                    }

                    // Check if price is near support
                    bool nearSupport = priceLevels
                        .Where(p => p.Type == "Support")
                        .Any(p => Math.Abs(currentPrice - p.Price) / p.Price < 0.005m && p.Strength >= 2);

                    // Check if price is near resistance
                    bool nearResistance = priceLevels
                        .Where(p => p.Type == "Resistance")
                        .Any(p => Math.Abs(currentPrice - p.Price) / p.Price < 0.005m && p.Strength >= 2);

                    if (nearResistance)
                    {
                        Console.WriteLine("Price is near strong resistance - avoiding entry");
                        lastTradeAttemptTime[symbol] = DateTime.Now;
                        continue;
                    }

                    // Check for news sentiment
                    bool sentimentOk = await SentimentAnalyzer.CheckForNegativeSentiment(symbol);
                    if (!sentimentOk)
                    {
                        Console.WriteLine($"Negative sentiment detected for {symbol}. Skipping for now.");
                        lastTradeAttemptTime[symbol] = DateTime.Now;
                        continue;
                    }

                    // Check order book structure for trading opportunities
                    var orderBookSignal = await OrderBookAnalyzer.AnalyzeOrderBook(client, symbol);
                    Console.WriteLine($"Order book analysis for {symbol}: {orderBookSignal}");

                    // Additional filtering for trade quality
                    if (orderBookSignal == OrderBookSignal.Sell ||
                        orderBookSignal == OrderBookSignal.StrongSell)
                    {
                        Console.WriteLine($"Skipping {symbol} - order book shows selling pressure");
                        lastTradeAttemptTime[symbol] = DateTime.Now;
                        continue;
                    }

                    // RELAXED CRITERIA: Allow more entries with neutral order book
                    if (orderBookSignal != OrderBookSignal.StrongBuy &&
                        orderBookSignal != OrderBookSignal.Buy &&
                        marketCondition.RSIVolatility > 55 && // Changed from 40 to 55
                        !nearSupport)
                    {
                        Console.WriteLine($"Skipping {symbol} - insufficient bullish signals");
                        lastTradeAttemptTime[symbol] = DateTime.Now;
                        continue;
                    }

                    // Verify one more time that we don't have a position (prevent race conditions)
                    bool alreadyTrading;
                    lock (tradeLock)
                    {
                        alreadyTrading = activePositions.ContainsKey(symbol) && activePositions[symbol];
                    }

                    if (alreadyTrading)
                    {
                        Console.WriteLine($"Race condition detected - already trading {symbol}, skipping");
                        continue;
                    }

                    // If all checks pass, mark this coin as having an active position
                    lock (tradeLock)
                    {
                        activePositions[symbol] = true;
                    }

                    // Launch a separate task to handle this trade
                    // This allows multiple coins to be traded concurrently
                    _ = Task.Run(async () => {
                        try
                        {
                            // Execute trading strategy with all the enhanced information
                            var tradeResult = await ScalpingStrategy.ExecuteEnhancedScalpingStrategy(
                                client,
                                symbol,
                                perCoinBudget,
                                stepSize,
                                minQuantity,
                                tickSize,
                                takerFee,
                                makerFee,
                                marketCondition,
                                priceLevels,
                                orderBookSignal
                            );

                            // Update statistics and performance metrics
                            if (tradeResult.Success)
                            {
                                totalTrades++;

                                // Update balance after trade
                                lock (tradeLock)
                                {
                                    // Only add to tracked profit - don't update budget directly
                                    totalProfit += tradeResult.Profit;

                                    if (tradeResult.Profit > 0)
                                    {
                                        profitableTrades++;
                                        consecutiveLosses = 0;
                                    }
                                    else
                                    {
                                        consecutiveLosses++;

                                        // Add to blacklist if we had a significant loss
                                        if (tradeResult.Profit < -0.3m)
                                        {
                                            tradingBlacklist[symbol] = DateTime.Now;
                                            Console.WriteLine($"Added {symbol} to trading blacklist due to loss of ${tradeResult.Profit:F4}");
                                        }
                                    }
                                }

                                // Update performance metrics
                                UpdatePerformanceMetrics(symbol, tradeResult);

                                Console.WriteLine($"Trade completed for {symbol}. Profit: ${tradeResult.Profit:F4}");
                                Console.WriteLine($"Total profit today: ${totalProfit:F4}");

                                lock (tradeLock)
                                {
                                    decimal winRate = totalTrades > 0 ? (decimal)profitableTrades / totalTrades : 0;
                                    Console.WriteLine($"Win rate: {winRate:P2}");
                                }

                                // Save performance data periodically
                                if (totalTrades % 3 == 0)
                                {
                                    SavePerformanceData();
                                    SaveRestrictedSymbols();
                                }
                            }
                            else if (tradeResult.ErrorMessage?.Contains("not whitelisted") == true ||
                                    tradeResult.ErrorMessage?.Contains("API-key restriction") == true)
                            {
                                // Handle API restricted symbols
                                Console.WriteLine($"Symbol {symbol} is not whitelisted for this API key");
                                lock (tradeLock)
                                {
                                    restrictedSymbols.Add(symbol);
                                    Console.WriteLine($"Added {symbol} to restricted symbols list");
                                    SaveRestrictedSymbols();
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error trading {symbol}: {ex.Message}");

                            // Check for API restrictions in exceptions
                            if (ex.Message.Contains("not whitelisted") || ex.Message.Contains("API-key restriction"))
                            {
                                lock (tradeLock)
                                {
                                    restrictedSymbols.Add(symbol);
                                    Console.WriteLine($"Added {symbol} to restricted symbols list due to API key restrictions");
                                    SaveRestrictedSymbols();
                                }
                            }
                        }
                        finally
                        {
                            // Mark position as inactive regardless of outcome
                            lock (tradeLock)
                            {
                                activePositions[symbol] = false;
                            }

                            // Update last attempt time
                            lastTradeAttemptTime[symbol] = DateTime.Now;
                        }
                    });

                    // Add delay between consecutive coin processing to avoid API rate limits
                    await Task.Delay(2000);
                }

                // Remove failed coins from the active list more frequently
                if (failedCoins.Count > 0)
                {
                    lock (tradeLock)
                    {
                        foreach (var failedCoin in failedCoins)
                        {
                            if (activeCoins.Contains(failedCoin) &&
                                (!activePositions.ContainsKey(failedCoin) || !activePositions[failedCoin]))
                            {
                                activeCoins.Remove(failedCoin);
                                Console.WriteLine($"Removed {failedCoin} from active coins due to unsuitability");
                            }
                        }
                        failedCoins.Clear();

                        // Trigger more frequent coin rotation if we removed coins
                        if (activeCoins.Count < effectiveMaxCoins)
                        {
                            lastCoinSelection = DateTime.MinValue; // Force selection on next iteration
                        }
                    }
                }

                // Check if we need to replace coins with unfavorable conditions
                if (activeCoins.Count < effectiveMaxCoins)
                {
                    // We have room for more coins
                    Console.WriteLine("Looking for additional coins to monitor...");
                    var additionalCoins = await CoinSelector.SelectBestTradingPairs(client, strategyPerformance, effectiveMaxCoins * 8);

                    // Filter out coins already in our list, blacklisted, restricted, or recently attempted
                    additionalCoins = additionalCoins
                        .Where(c => !activeCoins.Contains(c) &&
                                   !tradingBlacklist.ContainsKey(c) &&
                                   !restrictedSymbols.Contains(c) &&
                                   (!lastTradeAttemptTime.ContainsKey(c) ||
                                    DateTime.Now.Subtract(lastTradeAttemptTime[c]).TotalMinutes > 10))
                        .ToList();

                    // Add the best additional coins up to our max limit
                    foreach (var coin in additionalCoins.Take(effectiveMaxCoins - activeCoins.Count))
                    {
                        activeCoins.Add(coin);
                        Console.WriteLine($"Added {coin} to monitoring list to replace low volatility coin");
                    }
                }

                // FIXED Risk management checks - use proper calculation for daily loss limit
                if (totalProfit <= -maxDailyLoss)
                {
                    Console.WriteLine($"Daily loss limit reached (${totalProfit:F2} <= -${maxDailyLoss:F2}). Stopping trading for today.");
                    await Task.Delay(3600000 * 3); // Wait 3 hours before checking again
                    continue;
                }

                if (totalProfit >= maxDailyProfit)
                {
                    Console.WriteLine($"Daily profit target reached: ${totalProfit:F2}! Reducing position size to protect profits.");
                    budget = effectiveScalpingBalance * 0.5m; // Reduce position size after hitting target
                }

                if (consecutiveLosses >= maxConsecutiveLosses)
                {
                    Console.WriteLine($"Maximum consecutive losses ({maxConsecutiveLosses}) reached. Pausing for 1 hour.");
                    await Task.Delay(3600000); // Wait 1 hour  
                    consecutiveLosses = 0;
                }

                // Get updated account balance
                accountInfo = await client.SpotApi.Account.GetAccountInfoAsync();
                if (accountInfo.Success)
                {
                    var usdtBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
                    if (usdtBalance != null)
                    {
                        Console.WriteLine($"Current USDT Balance: {usdtBalance.Available:F8} (Free) + {usdtBalance.Locked:F8} (Locked)");
                        currentBalance = usdtBalance.Available; // Update the bot's knowledge of available budget
                    }
                }

                // Short delay before next cycle
                await Task.Delay(10000); // 10 seconds
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error occurred in main loop: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                await Task.Delay(60000); // Wait 1 minute before retrying
            }
        }
    }

    private static void UpdatePerformanceMetrics(string symbol, TradeResult tradeResult)
    {
        if (!strategyPerformance.ContainsKey(symbol))
        {
            strategyPerformance[symbol] = new StrategyMetrics();
        }

        var metrics = strategyPerformance[symbol];
        metrics.TotalTrades++;

        if (tradeResult.Profit > 0)
        {
            metrics.ProfitableTrades++;
        }

        metrics.TotalProfit += tradeResult.Profit;
        metrics.MaxProfit = Math.Max(metrics.MaxProfit, tradeResult.Profit);
        metrics.MaxLoss = Math.Min(metrics.MaxLoss, tradeResult.Profit);
        metrics.AvgProfit = metrics.TotalProfit / metrics.TotalTrades;
        metrics.WinRate = metrics.TotalTrades > 0 ?
            (decimal)metrics.ProfitableTrades / metrics.TotalTrades : 0;
    }

    private static void LoadPerformanceData()
    {
        try
        {
            if (File.Exists(performanceFilePath))
            {
                string json = File.ReadAllText(performanceFilePath);
                strategyPerformance = JsonSerializer.Deserialize<Dictionary<string, StrategyMetrics>>(json);
                Console.WriteLine($"Loaded performance data for {strategyPerformance.Count} symbols");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading performance data: {ex.Message}");
            strategyPerformance = new Dictionary<string, StrategyMetrics>();
        }
    }

    private static void SavePerformanceData()
    {
        try
        {
            string json = JsonSerializer.Serialize(strategyPerformance);
            File.WriteAllText(performanceFilePath, json);
            Console.WriteLine($"Saved performance data for {strategyPerformance.Count} symbols");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving performance data: {ex.Message}");
        }
    }

    // New methods to save/load arbitrage history
    private static void SaveArbitrageHistory()
    {
        try
        {
            string json = JsonSerializer.Serialize(arbitrageHistory);
            File.WriteAllText(arbitragePerformanceFilePath, json);
            Console.WriteLine($"Saved arbitrage history ({arbitrageHistory.Count} entries)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving arbitrage history: {ex.Message}");
        }
    }

    private static void LoadArbitrageHistory()
    {
        try
        {
            if (File.Exists(arbitragePerformanceFilePath))
            {
                string json = File.ReadAllText(arbitragePerformanceFilePath);
                arbitrageHistory = JsonSerializer.Deserialize<List<ArbitrageResult>>(json) ?? new List<ArbitrageResult>();

                // Calculate statistics from history
                totalArbitrageProfit = arbitrageHistory.Sum(a => a.Profit);
                successfulArbitrageTrades = arbitrageHistory.Count;

                Console.WriteLine($"Loaded {arbitrageHistory.Count} arbitrage records");
                Console.WriteLine($"Total historical arbitrage profit: {totalArbitrageProfit:F8} USDT");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading arbitrage history: {ex.Message}");
            arbitrageHistory = new List<ArbitrageResult>();
        }
    }

    // New methods to save/load restricted symbols
    private static void SaveRestrictedSymbols()
    {
        try
        {
            string json = JsonSerializer.Serialize(restrictedSymbols.ToList());
            File.WriteAllText("restricted_symbols.json", json);
            Console.WriteLine($"Saved {restrictedSymbols.Count} restricted symbols");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving restricted symbols: {ex.Message}");
        }
    }

    private static void LoadRestrictedSymbols()
    {
        try
        {
            if (File.Exists("restricted_symbols.json"))
            {
                string json = File.ReadAllText("restricted_symbols.json");
                var symbols = JsonSerializer.Deserialize<List<string>>(json);
                restrictedSymbols = new HashSet<string>(symbols);
                Console.WriteLine($"Loaded {restrictedSymbols.Count} restricted symbols");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading restricted symbols: {ex.Message}");
            restrictedSymbols = new HashSet<string>();
        }
    }
}