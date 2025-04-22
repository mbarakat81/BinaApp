using Binance.Net.Clients;
using Binance.Net.Enums;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Linq;
using Binance.Net.Objects.Models.Spot;
using TradingBot.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;

public class TriangularArbitrageSystem
{
    // Minimum profit threshold as a percentage (after fees)
    private const decimal MIN_PROFIT_THRESHOLD = 0.25m;
    // Maximum portion of available balance to use for arbitrage
    private const decimal MAX_BALANCE_ALLOCATION = 0.3m;
    // How many triangular paths to check
    private const int MAX_ARBITRAGE_PATHS = 20;
    // Cache file path
    private readonly string _cachePath = "arbitrage_cache.json";
    // Cache refresh interval in hours
    private const int CACHE_REFRESH_HOURS = 12;

    private readonly BinanceRestClient _client;
    private Dictionary<string, decimal> _tickSizes = new Dictionary<string, decimal>();
    private Dictionary<string, decimal> _fees = new Dictionary<string, decimal>();
    private DateTime _lastCacheUpdate = DateTime.MinValue;

    // Store common quote currencies to build triangular paths
    private readonly List<string> _baseQuotes = new List<string>
    {
        "USDT", "BTC", "ETH", "BNB", "BUSD"
    };

    public TriangularArbitrageSystem(BinanceRestClient client)
    {
        _client = client;
    }

    public async Task Initialize()
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Initializing arbitrage system...");

        // Try to load from cache first
        bool cacheLoaded = LoadCachedData();

        // If cache is missing, outdated, or empty, refresh it
        if (!cacheLoaded || DateTime.Now > _lastCacheUpdate.AddHours(CACHE_REFRESH_HOURS) ||
            _tickSizes.Count == 0 || _fees.Count == 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cache needs refresh. Fetching latest exchange data...");
            await RefreshCachedData();
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Loaded {_tickSizes.Count} symbols and {_fees.Count} fee entries from cache");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cache age: {(DateTime.Now - _lastCacheUpdate).TotalHours:F1} hours");
        }
    }
    private bool LoadCachedData()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return false;
            }

            string json = File.ReadAllText(_cachePath);
            var cacheData = JsonSerializer.Deserialize<ArbitrageCacheData>(json);

            if (cacheData == null)
            {
                return false;
            }

            _tickSizes = cacheData.TickSizes;
            _fees = cacheData.Fees;
            _lastCacheUpdate = cacheData.LastUpdated;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Successfully loaded cache from {_cachePath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error loading cache: {ex.Message}");
            return false;
        }
    }

    private async Task RefreshCachedData()
    {
        try
        {
            // Clear existing data
            _tickSizes.Clear();
            _fees.Clear();

            // Get exchange info
            var exchangeInfo = await _client.SpotApi.ExchangeData.GetExchangeInfoAsync();
            if (!exchangeInfo.Success)
            {
                throw new Exception("Failed to get exchange information");
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Processing {exchangeInfo.Data.Symbols.Count()} symbols...");

            // Get trading fees for the most common pairs first
            var commonPairs = new List<string> { "BTCUSDT", "ETHUSDT", "BNBUSDT", "ADAUSDT", "DOGEUSDT", "XRPUSDT" };
            foreach (var pair in commonPairs)
            {
                await AddFeeInfo(pair);

                // Add a small delay to avoid rate limits
                await Task.Delay(100);
            }

            // Process tick sizes for all symbols
            int processedCount = 0;
            int totalSymbols = exchangeInfo.Data.Symbols.Count();
            foreach (var symbol in exchangeInfo.Data.Symbols)
            {
                // Store tick sizes for price formatting
                var tickSizeFilter = symbol.Filters.FirstOrDefault(f => f.FilterType ==  SymbolFilterType.Price) as BinanceSymbolPriceFilter;
                if (tickSizeFilter != null)
                {
                    _tickSizes[symbol.Name] = tickSizeFilter.TickSize;
                }

                processedCount++;

                // Show progress every 200 symbols
                if (processedCount % 200 == 0 || processedCount == totalSymbols)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Processed {processedCount}/{totalSymbols} symbols");
                }
            }

            // Use default fee of 0.1% for pairs we didn't fetch specifically
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Using default fee (0.1%) for remaining symbols");

            // Update cache timestamp
            _lastCacheUpdate = DateTime.Now;

            // Save to cache file
            SaveCachedData();

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cache refreshed with {_tickSizes.Count} symbols and {_fees.Count} fee entries");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error refreshing cache: {ex.Message}");
            throw;
        }
    }

    private async Task AddFeeInfo(string symbol)
    {
        try
        {
            var feeInfo = await _client.SpotApi.Account.GetTradeFeeAsync(symbol);
            if (feeInfo.Success && feeInfo.Data.Any())
            {
                _fees[symbol] = feeInfo.Data.First().TakerFee;
            }
            else
            {
                _fees[symbol] = 0.1m; // Default to 0.1% if we can't get the fee
            }
        }
        catch
        {
            _fees[symbol] = 0.1m; // Default to 0.1% if there's an error
        }
    }

    private void SaveCachedData()
    {
        try
        {
            var cacheData = new ArbitrageCacheData
            {
                TickSizes = _tickSizes,
                Fees = _fees,
                LastUpdated = _lastCacheUpdate
            };

            string json = JsonSerializer.Serialize(cacheData);
            File.WriteAllText(_cachePath, json);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Cache saved to {_cachePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error saving cache: {ex.Message}");
        }
    }
    public async Task<ArbitrageResult> CheckAndExecuteArbitrage(decimal availableBalance)
    {
        // Get all current prices
        var tickers = await _client.SpotApi.ExchangeData.GetTickersAsync();
        if (!tickers.Success)
        {
            return new ArbitrageResult { Success = false, Message = "Failed to fetch prices" };
        }

        // Build price dictionary for quick lookups
        var prices = tickers.Data.Where(w=> w.LastPrice > 0).ToDictionary(t => t.Symbol, t => t.LastPrice);

        // Generate potential triangular paths
        var arbitragePaths = GenerateArbitragePaths(prices);

        // Find the most profitable path
        var bestPath = arbitragePaths
            .OrderByDescending(p => p.ExpectedProfitPercent)
            .FirstOrDefault();

        if (bestPath == null || bestPath.ExpectedProfitPercent < MIN_PROFIT_THRESHOLD)
        {
            return new ArbitrageResult
            {
                Success = false,
                Message = $"No profitable arbitrage found. Best: {bestPath?.ExpectedProfitPercent:F2}%"
            };
        }

        // Calculate trade amount based on maximum allocation and available balance
        decimal tradeAmount = Math.Min(availableBalance * MAX_BALANCE_ALLOCATION,
                                       availableBalance * 0.95m); // Use at most 95% of available balance

        // Execute the trades
        var executionResult = await ExecuteArbitragePath(bestPath, tradeAmount);

        return executionResult;
    }

    private List<ArbitragePath> GenerateArbitragePaths(Dictionary<string, decimal> prices)
    {
        var result = new List<ArbitragePath>();
        var allSymbols = prices.Keys.ToList();

        // Start with USDT as base (or other stablecoins)
        foreach (var startQuote in new[] { "USDT", "BUSD", "USDC" })
        {
            // Find all pairs with this quote currency
            var firstLegPairs = allSymbols
                .Where(s => s.EndsWith(startQuote))
                .Take(15) // Limit to top pairs
                .ToList();

            foreach (var firstLegPair in firstLegPairs)
            {
                // Extract the base currency from the pair (e.g., "BTC" from "BTCUSDT")
                string secondCurrency = firstLegPair.Replace(startQuote, "");

                // Find all pairs with the second currency as quote
                var secondLegPairs = allSymbols
                    .Where(s => s.EndsWith(secondCurrency) && !s.StartsWith(startQuote))
                    .Take(10) // Limit to top pairs
                    .ToList();

                foreach (var secondLegPair in secondLegPairs)
                {
                    // Extract the base currency from the second pair (e.g., "ETH" from "ETHBTC")
                    string thirdCurrency = secondLegPair.Replace(secondCurrency, "");

                    // Check if we can go back to the start currency
                    string thirdLegPair = $"{thirdCurrency}{startQuote}";

                    if (prices.ContainsKey(thirdLegPair))
                    {
                        // Found a triangular path
                        decimal profitPercent = CalculateArbitrageProfitPercent(
                            startQuote, secondCurrency, thirdCurrency,
                            firstLegPair, secondLegPair, thirdLegPair,
                            prices);

                        result.Add(new ArbitragePath
                        {
                            StartCurrency = startQuote,
                            SecondCurrency = secondCurrency,
                            ThirdCurrency = thirdCurrency,
                            FirstPair = firstLegPair,
                            SecondPair = secondLegPair,
                            ThirdPair = thirdLegPair,
                            ExpectedProfitPercent = profitPercent
                        });
                    }
                }
            }
        }

        return result.OrderByDescending(p => p.ExpectedProfitPercent)
                     .Take(MAX_ARBITRAGE_PATHS)
                     .ToList();
    }

    private decimal CalculateArbitrageProfitPercent(
    string startCurrency, string secondCurrency, string thirdCurrency,
    string firstPair, string secondPair, string thirdPair,
    Dictionary<string, decimal> prices)
    {
        // Get prices for each step
        if (!prices.TryGetValue(firstPair, out decimal firstPrice) ||
            !prices.TryGetValue(secondPair, out decimal secondPrice) ||
            !prices.TryGetValue(thirdPair, out decimal thirdPrice))
        {
            return -100; // Indicate invalid path
        }

        // Get fees for each trade - use default 0.1% if not available
        decimal firstFee = _fees.ContainsKey(firstPair) ? _fees[firstPair] * 100 : 0.1m;
        decimal secondFee = _fees.ContainsKey(secondPair) ? _fees[secondPair] * 100 : 0.1m;
        decimal thirdFee = _fees.ContainsKey(thirdPair) ? _fees[thirdPair] * 100 : 0.1m;

        // Start with 1 unit of start currency
        decimal amount = 1.0m;

        // Calculate conversions including fees
        // First trade: startCurrency → secondCurrency
        amount = firstPair.StartsWith(startCurrency) ?
                amount / firstPrice * (1 - firstFee / 100) : // Selling start for second
                amount * firstPrice * (1 - firstFee / 100);  // Buying second with start

        // Second trade: secondCurrency → thirdCurrency
        amount = secondPair.StartsWith(secondCurrency) ?
                amount / secondPrice * (1 - secondFee / 100) : // Selling second for third
                amount * secondPrice * (1 - secondFee / 100);  // Buying third with second

        // Third trade: thirdCurrency → startCurrency
        amount = thirdPair.StartsWith(thirdCurrency) ?
                amount / thirdPrice * (1 - thirdFee / 100) : // Selling third for start
                amount * thirdPrice * (1 - thirdFee / 100);  // Buying start with third

        // Calculate profit percentage
        decimal profitPercent = (amount - 1.0m) * 100;

        return profitPercent;
    }

    private async Task<ArbitrageResult> ExecuteArbitragePath(ArbitragePath path, decimal startAmount)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Executing arbitrage: {path.StartCurrency} → {path.SecondCurrency} → {path.ThirdCurrency} → {path.StartCurrency}");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Expected profit: {path.ExpectedProfitPercent:F2}%");

        decimal firstAmount = startAmount;
        decimal secondAmount = 0;
        decimal thirdAmount = 0;
        decimal finalAmount = 0;

        try
        {
            // First trade
            var firstTradeResult = await ExecuteArbitrageTrade(path.FirstPair, firstAmount, path.StartCurrency, path.SecondCurrency);
            if (!firstTradeResult.Success)
            {
                return new ArbitrageResult { Success = false, Message = $"First trade failed: {firstTradeResult.Message}" };
            }
            secondAmount = firstTradeResult.ReceivedAmount;

            // Second trade
            var secondTradeResult = await ExecuteArbitrageTrade(path.SecondPair, secondAmount, path.SecondCurrency, path.ThirdCurrency);
            if (!secondTradeResult.Success)
            {
                return new ArbitrageResult { Success = false, Message = $"Second trade failed: {secondTradeResult.Message}" };
            }
            thirdAmount = secondTradeResult.ReceivedAmount;

            // Third trade
            var thirdTradeResult = await ExecuteArbitrageTrade(path.ThirdPair, thirdAmount, path.ThirdCurrency, path.StartCurrency);
            if (!thirdTradeResult.Success)
            {
                return new ArbitrageResult { Success = false, Message = $"Third trade failed: {thirdTradeResult.Message}" };
            }
            finalAmount = thirdTradeResult.ReceivedAmount;

            // Calculate actual profit
            decimal actualProfit = finalAmount - firstAmount;
            decimal actualProfitPercent = (actualProfit / firstAmount) * 100;

            return new ArbitrageResult
            {
                Success = true,
                StartAmount = firstAmount,
                FinalAmount = finalAmount,
                Profit = actualProfit,
                ProfitPercent = actualProfitPercent,
                Message = $"Arbitrage completed. Profit: {actualProfit:F8} {path.StartCurrency} ({actualProfitPercent:F2}%)",
                Path = $"{path.StartCurrency} → {path.SecondCurrency} → {path.ThirdCurrency} → {path.StartCurrency}"
            };
        }
        catch (Exception ex)
        {
            return new ArbitrageResult
            {
                Success = false,
                Message = $"Error executing arbitrage: {ex.Message}",
                StartAmount = firstAmount,
                FinalAmount = finalAmount
            };
        }
    }

    private async Task<TradeResult> ExecuteArbitrageTrade(string symbol, decimal amount, string fromCurrency, string toCurrency)
    {
        // Determine trade direction
        bool isBuy = symbol.EndsWith(fromCurrency);
        OrderSide side = isBuy ? OrderSide.Buy : OrderSide.Sell;

        // Get market info for the pair
        var exchangeInfo = await _client.SpotApi.ExchangeData.GetExchangeInfoAsync(symbol);
        if (!exchangeInfo.Success)
        {
            return new TradeResult { Success = false, Message = $"Failed to get exchange info: {exchangeInfo.Error?.Message}" };
        }

        // Get symbol info to determine the correct precision
        var symbolInfo = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
        if (symbolInfo == null)
        {
            return new TradeResult { Success = false, Message = $"Symbol info not found for {symbol}" };
        }

        // Get appropriate price and quantity precision
        var lotSizeFilter = symbolInfo.LotSizeFilter;
        decimal stepSize = lotSizeFilter?.StepSize ?? 0.00001m;
        int quantityPrecision = GetPrecisionFromStepSize(stepSize);

        var priceFilter = symbolInfo.PriceFilter;
        decimal tickSize = priceFilter?.TickSize ?? 0.00001m;
        int pricePrecision = GetPrecisionFromStepSize(tickSize);

        // For quote order qty (USDT amount), use typically 2-4 decimal places max
        int quoteOrderPrecision = 2; // Default for USDT

        // Check for specific rules on the quoteOrderQty precision
        // Some exchanges have specific rules for quote precision
        if (fromCurrency == "USDT" || fromCurrency == "BUSD" || fromCurrency == "USDC")
        {
            quoteOrderPrecision = 2; // Stablecoins often use 2 decimal places
        }
        else if (fromCurrency == "BTC")
        {
            quoteOrderPrecision = 6; // BTC often allows 6 decimal places
        }
        else if (fromCurrency == "ETH")
        {
            quoteOrderPrecision = 5; // ETH often allows 5 decimal places
        }
        else
        {
            quoteOrderPrecision = 4; // Default for other currencies
        }

        // Get market info for the pair
        var orderBookResult = await _client.SpotApi.ExchangeData.GetOrderBookAsync(symbol, 5);
        if (!orderBookResult.Success)
        {
            return new TradeResult { Success = false, Message = $"Failed to get order book: {orderBookResult.Error?.Message}" };
        }

        // Get current best price
        decimal price = side == OrderSide.Buy ? orderBookResult.Data.Asks.First().Price : orderBookResult.Data.Bids.First().Price;

        // Calculate quantity based on trade direction
        decimal quantity;
        decimal quoteOrderQty = 0;

        if (side == OrderSide.Buy)
        {
            // Round down the quote amount (e.g., USDT amount) to avoid precision errors
            quoteOrderQty = Math.Floor(amount * (decimal)Math.Pow(10, quoteOrderPrecision)) / (decimal)Math.Pow(10, quoteOrderPrecision);

            // Calculate the quantity for logging purposes
            quantity = quoteOrderQty / price;
            quantity = Math.Floor(quantity * (decimal)Math.Pow(10, quantityPrecision)) / (decimal)Math.Pow(10, quantityPrecision);

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Buy {quantity} {toCurrency} with {quoteOrderQty} {fromCurrency}");
        }
        else
        {
            // For sell orders, round down the quantity to avoid precision errors
            quantity = Math.Floor(amount * (decimal)Math.Pow(10, quantityPrecision)) / (decimal)Math.Pow(10, quantityPrecision);
            quoteOrderQty = 0; // Not used for sell orders

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sell {quantity} {fromCurrency} for {toCurrency}");
        }

        try
        {
            // Execute the trade
            var orderResult = await _client.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                side,
                SpotOrderType.Market,
                quantity: side == OrderSide.Sell ? quantity : (decimal?)null,
                quoteQuantity: side == OrderSide.Buy ? quoteOrderQty : (decimal?)null);

            if (!orderResult.Success)
            {
                return new TradeResult { Success = false, Message = $"Order failed: {orderResult.Error?.Message}" };
            }

            // Check order status
            var orderStatus = await _client.SpotApi.Trading.GetOrderAsync(symbol, orderResult.Data.Id);
            if (!orderStatus.Success || orderStatus.Data.Status != OrderStatus.Filled)
            {
                return new TradeResult { Success = false, Message = "Order not filled immediately" };
            }

            // Calculate received amount
            decimal receivedAmount = side == OrderSide.Buy ?
                orderStatus.Data.QuantityFilled : // If buying, we received this many tokens
                orderStatus.Data.QuoteQuantityFilled; // If selling, we received this much quote currency

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trade successful. Received: {receivedAmount} {(side == OrderSide.Buy ? toCurrency : fromCurrency)}");

            return new TradeResult
            {
                Success = true,
                ReceivedAmount = receivedAmount,
                Message = $"Trade executed at price {orderStatus.Data.Price}"
            };
        }
        catch (Exception ex)
        {
            return new TradeResult { Success = false, Message = $"Exception during order execution: {ex.Message}" };
        }
    }
    // Helper method to determine precision from step size
    private int GetPrecisionFromStepSize(decimal stepSize)
    {
        string stepSizeStr = stepSize.ToString(CultureInfo.InvariantCulture);
        int precision = 0;

        if (stepSizeStr.Contains('.'))
        {
            string[] parts = stepSizeStr.Split('.');
            if (parts.Length > 1)
            {
                string decimalPart = parts[1].TrimEnd('0');
                if (decimalPart.Length > 0)
                {
                    // Find the position of the first non-zero digit
                    for (int i = 0; i < decimalPart.Length; i++)
                    {
                        if (decimalPart[i] != '0')
                        {
                            precision = i + 1;
                            break;
                        }
                        precision = i + 1;
                    }
                }
            }
        }

        return precision;
    }
}

public class ArbitragePath
{
    public string StartCurrency { get; set; }
    public string SecondCurrency { get; set; }
    public string ThirdCurrency { get; set; }
    public string FirstPair { get; set; }
    public string SecondPair { get; set; }
    public string ThirdPair { get; set; }
    public decimal ExpectedProfitPercent { get; set; }
}
public class ArbitrageCacheData
{
    public Dictionary<string, decimal> TickSizes { get; set; } = new Dictionary<string, decimal>();
    public Dictionary<string, decimal> Fees { get; set; } = new Dictionary<string, decimal>();
    public DateTime LastUpdated { get; set; }
}
public class ArbitrageResult
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public decimal StartAmount { get; set; }
    public decimal FinalAmount { get; set; }
    public decimal Profit { get; set; }
    public decimal ProfitPercent { get; set; }
    public string Path { get; set; }
}

//public class TradeResult
//{
//    public bool Success { get; set; }
//    public string Message { get; set; }
//    public decimal ReceivedAmount { get; set; }
//}
