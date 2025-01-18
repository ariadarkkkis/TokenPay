using Flurl;
using Flurl.Http;
using FreeSql;
using HDWallet.Tron;
using Nethereum.Signer;
using Org.BouncyCastle.Asn1.X509;
using System.Collections.Generic;
using TokenPay.Domains;
using TokenPay.Extensions;
using TokenPay.Helper;
using TokenPay.Models.EthModel;

namespace TokenPay.BgServices
{
    public class CollectionTRONService : BaseScheduledService
    {
        private readonly IConfiguration _configuration;
        private readonly TelegramBot _bot;
        private readonly IFreeSql freeSql;
        /// <summary>
        /// 是否启用归集功能
        /// </summary>
        private bool Enable => _configuration.GetValue("Collection:Enable", false);
        /// <summary>
        /// 是否启用能量租赁
        /// </summary>
        private bool UseEnergy => _configuration.GetValue("Collection:UseEnergy", true);
        /// <summary>
        /// 每次归集操作强制检查所有地址余额
        /// </summary>
        private bool ForceCheckAllAddress => _configuration.GetValue("Collection:ForceCheckAllAddress", false);
        /// <summary>
        /// 是否保留0.000001USDT
        /// </summary>
        private bool RetainUSDT => _configuration.GetValue("Collection:RetainUSDT", true);
        /// <summary>
        /// 最小归集USDT
        /// </summary>
        private decimal MinUSDT => _configuration.GetValue("Collection:MinUSDT", 0.1m);
        /// <summary>
        /// 消耗能量数量（请勿修改）
        /// </summary>
        private long DefaultNeedEnergy => _configuration.GetValue("Collection:NeedEnergy", 64285);
        /// <summary>
        /// 最低租赁能量数量（请勿修改）
        /// </summary>
        private long EnergyMinValue => _configuration.GetValue("Collection:EnergyMinValue", 64400);
        /// <summary>
        /// 当前能量单价（请勿修改）
        /// </summary>
        private decimal EnergyPrice => _configuration.GetValue("Collection:EnergyPrice", 210m);
        /// <summary>
        /// 租赁能量时长（请勿修改）
        /// </summary>
        private int RentDuration => _configuration.GetValue("Collection:RentDuration", 10);
        /// <summary>
        /// 租赁能量时长单位（请勿修改）
        /// </summary>
        private string RentTimeUnit => _configuration.GetValue("Collection:RentTimeUnit", "m")!;
        /// <summary>
        /// 归集收款地址
        /// </summary>
        private string Address => _configuration.GetValue<string>("Collection:Address")!;
        private int CheckTime => _configuration.GetValue("Collection:CheckTime", 3);
        /// <summary>
        /// 预估带宽消耗的TRX
        /// </summary>
        private decimal NetUsedTrx => 0.3m;
        private EnergyApi energyApi => new EnergyApi(_logger, _configuration);
        public CollectionTRONService(
            IConfiguration configuration,
            TelegramBot bot,
            IFreeSql freeSql,
            ILogger<CollectionTRONService> logger) : base("TRON collection task", TimeSpan.FromHours(configuration.GetValue("Collection:CheckTime", 1)), logger)
        {
            this._configuration = configuration;
            this._bot = bot;
            this.freeSql = freeSql;
        }
        protected override async Task ExecuteAsync(DateTime RunTime, CancellationToken stoppingToken)
        {
            if (!Enable) return;
            var SendToTelegram = false;
            if (!File.Exists("FeeWalletPrivateKey.txt"))
            {
                var ecKey = Nethereum.Signer.EthECKey.GenerateKey();
                var rawPrivateKey = ecKey.GetPrivateKeyAsBytes();
                var hex = Convert.ToHexString(rawPrivateKey);
                File.WriteAllText("FeeWalletPrivateKey.txt", hex);
                SendToTelegram = true;
            }
            var privateKey = File.ReadAllText("FeeWalletPrivateKey.txt");
            var mainWallet = new TronWallet(privateKey);
            _logger.LogInformation("The fee wallet address is: {a}", mainWallet.Address);
            if (SendToTelegram)
            {
                await _bot.SendTextMessageAsync(@$"<b>Create a fee wallet</b>

Fee wallet address: <code>{mainWallet.Address}</code>
Fee wallet private key: <tg-spoiler>
{privateKey.Substring(0, 32)}
{privateKey.Substring(32, 32)}
</tg-spoiler>
Please do not copy this private key unless necessary!!!
To avoid theft, the private key has been split into two parts. Please copy them separately.
<b>Please transfer TRX to this address for collecting USDT</b>
");
            }
            var mainTrx = await QueryTronAction.GetTRXAsync(mainWallet.Address);
            _logger.LogInformation("Current TRX balance in the fee wallet: {trx}", mainTrx);
            if (mainTrx < 1)
            {
                while (!stoppingToken.IsCancellationRequested && mainTrx < 1)
                {
                    var TrxCheckTime = 10;
                    _logger.LogInformation("The fee wallet address is: {a}", mainWallet.Address);
                    _logger.LogInformation("Waiting for TRX to be transferred to the fee wallet");
                    mainTrx = await QueryTronAction.GetTRXAsync(mainWallet.Address);
                    if (mainTrx > 1)
                        _logger.LogInformation("Transfer completed, current TRX balance: {trx}", mainTrx);
                    else
                    {
                        await _bot.SendTextMessageAsync(@$"The fee wallet address needs to be topped up with TRX

Fee wallet address: <code>{mainWallet.Address}</code>
Current TRX balance: {mainTrx} TRX


Please deposit TRX, balance check will be expire after {TrxCheckTime} seconds.

If you do not need to use the collection function, please configure <b>Collection:Enable</b> in the configuration file to <b>false</b>");                    }
                    await Task.Delay(TrxCheckTime * 1000);
                }
            }
            try
            {
                Address.Base58ToHex();
            }
            catch (Exception)
            {
                _logger.LogError("The collection payment address {a} is incorrect!", Address);
                await _bot.SendTextMessageAsync(@$"The collection address is incorrect, please check

Collection payment address: <code>{Address}</code>");
                return;
            }
            var usdt = await QueryTronAction.GetUsdtAmountAsync(Address);
            if (usdt <= 0)
            {
                _logger.LogError("The collection payment address {a} must have USDT!", Address);
                await _bot.SendTextMessageAsync(@$"The collection address must have USDT

Collection payment address: <code>{Address}</code>");
                return;
            }
            else
            {
                var trx = await QueryTronAction.GetTRXAsync(Address);
                _logger.LogInformation("Collection payment address, current TRX balance: {trx}, current USDT balance: {usdt}", trx, usdt);
                await _bot.SendTextMessageAsync(@$"Collect the balance of the receiving address

Collection payment address: <code>{Address}</code>
Current TRX balance: {trx} TRX
Current USDT balance: {usdt} USDT");
            }
            var _repository = freeSql.GetRepository<Tokens>();
            var list = await _repository.Where(x => x.Currency == TokenCurrency.TRX).Where(x => ForceCheckAllAddress || (x.USDT > MinUSDT || x.Value > 0.5m)).ToListAsync();
            var count = 0;
            foreach (var item in list)
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (item.LastCheckTime.HasValue && (DateTime.Now - item.LastCheckTime.Value).TotalHours <= 1)
                {
                    //避免短时间重复检查余额
                    continue;
                }
                var TRX = await QueryTronAction.GetTRXAsync(item.Address);
                var USDT = await QueryTronAction.GetUsdtAmountAsync(item.Address);
                item.Value = TRX;
                item.USDT = USDT;
                item.LastCheckTime = DateTime.Now;
                await _repository.UpdateAsync(item);
                _logger.LogInformation("Update address balance data: {a}/{b}, TRX: {TRX}, USDT: {USDT}", ++count, list.Count, TRX, USDT);
                await Task.Delay(1500);
            }
            list = await _repository.Where(x => x.Currency == TokenCurrency.TRX).Where(x => x.USDT > MinUSDT || x.Value > 0.5m).ToListAsync();
            _logger.LogInformation(@"A total of {count} addresses that need to be collected were found. There are {a} addresses with TRX, with a total of {b} TRX, and {c} addresses with USDT, with a total of {d} USDT.",
                list.Count,
                list.Where(x => x.Value > 0.5m).Count(),
                list.Where(x => x.Value > 0.5m).Sum(x => x.Value),
                list.Where(x => x.USDT > MinUSDT).Count(),
                list.Where(x => x.USDT > MinUSDT).Sum(x => x.USDT));
            Func<int, Task<(decimal, string)>> GetPrice = async (int ResourceValue) =>
            {
                var resp = await energyApi.OrderPrice(ResourceValue, RentDuration, RentTimeUnit);
                _logger.LogInformation("Energy price forecast: {@result}", resp);
                if (resp != null && resp.Code == 0)
                {
                    var EnergyPayAddress = resp.Data.PayAddress;
                    var EnergyPrice = resp.Data.Price;
                    return (EnergyPrice, EnergyPayAddress);
                }
                _logger.LogError("Energy price forecast failed!");
                await _bot.SendTextMessageAsync(@$"Energy price forecast failed!

Energy quantity: {ResourceValue}");
                return (0, string.Empty);
            };
            _logger.LogInformation("------------------------------");
            if (list.Where(x => x.Value > 0.5m).Any())
                _logger.LogInformation("Start collecting TRX");
            else
                _logger.LogInformation("Skip collecting TRX");
            foreach (var item in list.Where(x => x.Value > 0.5m))
            {
                if (stoppingToken.IsCancellationRequested) return;
                var wallet = new TronWallet(item.Key);

                var account = await QueryTronAction.GetAccountResourceAsync(wallet.Address);
                if (account.FreeNetLimit - account.FreeNetUsed < 280)
                {
                    continue;
                }
                var (success, txid) = await wallet.TransferTrxAsync(item.Value, Address);
                if (success)
                {
                    _logger.LogInformation("TRX collection is successful, TRX: {a}, Txid: {b}", item.Value, txid);
                    item.Value = 0;
                    await _repository.UpdateAsync(item);
                    await _bot.SendTextMessageAsync(@$"TRX collection successful!

Collection address: <code>{item.Address}</code>
Number of collections: {item.Value} TRX
Transaction Hash: {txid} <b><a href=""https://tronscan.org/#/transaction/{txid}?lang=en"">View transaction</a></b>");
                }
                else
                {
                    _logger.LogWarning("Failed to collect TRX. Reason for failure: {b}", txid);
                }
            }
            _logger.LogInformation("------------------------------");
            if (list.Where(x => x.USDT > MinUSDT).Any())
                _logger.LogInformation("Start collecting USDT");
            else
                _logger.LogInformation("Skip collecting USDT");
            foreach (var item in list.Where(x => x.USDT > MinUSDT))
            {
                if (stoppingToken.IsCancellationRequested) return;
                var wallet = new TronWallet(item.Key);
                var account = await QueryTronAction.GetAccountAsync(wallet.Address);
                if (account.CreateTime == 0)
                {
                    _logger.LogInformation("The address is not activated. Activate: {a}", wallet.Address);

                    if (!await CheckMainWalletTrx(mainWallet, NetUsedTrx))
                    {
                        return;
                    }
                    var (success2, txid3) = await mainWallet.TransferTrxAsync(0.000001m, wallet.Address);
                    if (success2)
                    {
                        _logger.LogInformation("Activation successful, address: {a}", wallet.Address);
                    }
                    else
                    {
                        _logger.LogWarning("Activation failed, skip this address, address: {a}", wallet.Address);
                        continue;
                    }
                }
                var NeedEnergy = DefaultNeedEnergy;
                var accountResource = await QueryTronAction.GetAccountResourceAsync(wallet.Address);
                var needNet = accountResource.FreeNetLimit - accountResource.FreeNetUsed < 400;
                var energy = accountResource.EnergyLimit - accountResource.EnergyUsed;
                NeedEnergy -= energy;
                if (NeedEnergy > 0)
                {
                    if (!UseEnergy)
                    {
                        var trx = NeedEnergy * EnergyPrice / 1_000_000;
                        if (needNet)
                        {
                            trx += 0.5m;
                        }
                        var nowTrx = await QueryTronAction.GetTRXAsync(wallet.Address);
                        if (nowTrx < trx)
                        {
                            if (!await CheckMainWalletTrx(mainWallet, trx - nowTrx + NetUsedTrx))
                            {
                                return;
                            }
                            var (success2, txid3) = await mainWallet.TransferTrxAsync(trx - nowTrx, wallet.Address);
                            if (success2)
                            {
                                _logger.LogInformation("Transfer fee successful, address: {a}", wallet.Address);
                            }
                            else
                            {
                                _logger.LogWarning("Transfer fee failed, skip this address, address: {a}", wallet.Address);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        if (needNet)
                        {
                            var trx = 0.5m;
                            var nowTrx = await QueryTronAction.GetTRXAsync(wallet.Address);
                            if (nowTrx < trx)
                            {
                                if (!await CheckMainWalletTrx(mainWallet, trx + NetUsedTrx))
                                {
                                    return;
                                }
                                var (success2, txid3) = await mainWallet.TransferTrxAsync(trx - nowTrx, wallet.Address);
                                if (success2)
                                {
                                    _logger.LogInformation("Transfer fee successful, address: {a}", wallet.Address);
                                }
                                else
                                {
                                    _logger.LogWarning("Transfer fee failed, skip this address, address: {a}", wallet.Address);
                                    continue;
                                }
                            }
                        }
                        if (NeedEnergy < EnergyMinValue)
                        {
                            NeedEnergy = EnergyMinValue;
                        }
                        var (amountTrx, PaymentAddress) = await GetPrice((int)NeedEnergy);
                        if (amountTrx == 0)
                        {
                            _logger.LogWarning("Energy price estimation failed! Energy quantity: {a}", NeedEnergy);
                            continue;
                        }
                        if (!await CheckMainWalletTrx(mainWallet, amountTrx + NetUsedTrx))
                        {
                            return;
                        }
                        var (success3, msg, txn) = await QueryTronAction.GetTransferTrxSignedTxnToJobjectAsync(
                            mainWallet.Address,
                            privateKey,
                            amountTrx,
                            PaymentAddress);
                        if (success3)
                        {
                            var CreateModel = new CreateOrderModel
                            {
                                PayAddress = mainWallet.Address,
                                PayAmount = amountTrx,
                                ReceiveAddress = wallet.Address,
                                RentDuration = RentDuration,
                                RentTimeUnit = RentTimeUnit,
                                ResourceValue = (int)NeedEnergy,
                                SignedTxn = txn!
                            };
                            var feeResult = await energyApi.CreateOrder(CreateModel);
                            if (feeResult.Code == 0)
                            {
                                var count2 = 0;
                                await Task.Delay(3000);
                                while (!stoppingToken.IsCancellationRequested && count2 < 30)
                                {
                                    try
                                    {
                                        var feeResult2 = await energyApi.OrderQuery(feeResult.Data.OrderNo);
                                        if (feeResult2.Code == 0)
                                        {
                                            if (feeResult2.Data.Status == FeeeOrderStatus.Submitted)
                                            {
                                                count = 30;
                                                count2 = 30;
                                                var TxId = string.Empty;

                                                var count3 = 0;
                                                while (!stoppingToken.IsCancellationRequested && count3 < 5)
                                                {
                                                    try
                                                    {
                                                        accountResource = await QueryTronAction.GetAccountResourceAsync(wallet.Address);
                                                        energy = accountResource.EnergyLimit - accountResource.EnergyUsed;
                                                        if (energy >= DefaultNeedEnergy)
                                                        {
                                                            count3 = 5;
                                                            _logger.LogInformation("Energy leasing successful, current energy: {e}, address: {a}", energy, wallet.Address);
                                                            break;
                                                        }
                                                    }
                                                    catch (Exception)
                                                    {

                                                    }
                                                    finally
                                                    {
                                                        if (count3 < 5)
                                                            await Task.Delay(3000);
                                                        count3++;
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            _logger.LogWarning("Failed to query energy order information. Reason for failure: {msg}", feeResult2.Msg);
                                            continue;
                                        }
                                    }
                                    catch (Exception e)
                                    {
                                        _logger.LogWarning("Failed to query energy order information. Reason for failure: {msg}", e.Message);
                                        continue;
                                    }
                                    finally
                                    {
                                        if (count2 < 30)
                                            await Task.Delay(1000 * 3);
                                        count2++;
                                    }
                                }
                            }
                            else
                            {
                                CreateModel.SignedTxn = new object();
                                _logger.LogWarning("Failed to collect USDT, failed to lease energy, reason for failure: {msg}\nRequest parameters: {@CreateModel}", feeResult.Msg, CreateModel);
                                continue;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Failed to collect USDT, reason for failure: {b}", "Energy rental failed! " + msg);
                            continue;
                        }
                    }
                }
                else
                {
                    if (needNet)
                    {
                        var trx = 0.5m;
                        var nowTrx = await QueryTronAction.GetTRXAsync(wallet.Address);
                        if (nowTrx < trx)
                        {
                            if (!await CheckMainWalletTrx(mainWallet, trx - nowTrx + NetUsedTrx))
                            {
                                return;
                            }
                            var (success2, txid3) = await mainWallet.TransferTrxAsync(trx - nowTrx, wallet.Address);
                            if (success2)
                            {
                                _logger.LogInformation("Transfer fee successful, address: {a}", wallet.Address);
                            }
                            else
                            {
                                _logger.LogWarning("Transfer fee failed, skip this address, address: {a}", wallet.Address);
                                continue;
                            }
                        }
                    }
                }
                var RetainUSDTAmount = RetainUSDT ? 0.000001m : 0;
                var (success, txid) = await wallet.TransferUSDTAsync(item.USDT - RetainUSDTAmount, Address);
                if (success)
                {
                    _logger.LogInformation("USDT collection is successful, USDT: {a}, Txid: {b}", item.USDT - RetainUSDTAmount, txid);
                    await _bot.SendTextMessageAsync(@$"USDT collection successful!

Collection address: <code>{item.Address}</code>
Number of collections: {item.USDT - RetainUSDTAmount} USDT
Transaction Hash: {txid} <b><a href=""https://tronscan.org/#/transaction/{txid}?lang=en"">View transaction</a></b>");
                    item.USDT = 0;
                    await _repository.UpdateAsync(item);
                }
                else
                {
                    _logger.LogWarning("Failed to collect USDT, reason for failure: {b}", txid);
                }
            }
        }
        /// <summary>
        /// 检查手续费钱包TRX余额是否充足
        /// </summary>
        /// <param name="mainWallet"></param>
        /// <param name="minTrx"></param>
        /// <returns></returns>
        private async Task<bool> CheckMainWalletTrx(TronWallet mainWallet, decimal minTrx)
        {
            var mainTrx = await QueryTronAction.GetTRXAsync(mainWallet.Address);
            if (mainTrx < minTrx)
            {
                _logger.LogWarning("Insufficient TRX in fee wallet! Required TRX: {minTrx}, current TRX: {mainTrx}", minTrx, mainTrx);
                await _bot.SendTextMessageAsync(@$"The fee wallet does not have enough TRX, so the collection task cannot be continued!

Fee wallet address: <code>{mainWallet.Address}</code>
Current TRX balance: {mainTrx} TRX


Please deposit TRX first, the collection task will expire after {CheckTime} hours.");
                return false;
            }
            return true;
        }
    }
}
