using NBitcoin;
using NBitcoin.Policy;
using System;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Exceptions;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;

namespace WalletWasabi.Blockchain.Transactions
{
	public class TransactionFactory
	{
		public Network Network { get; }
		public KeyManager KeyManager { get; }
		public ICoinsView Coins { get; }
		public string Password { get; }
		public bool AllowUnconfirmed { get; }

		/// <param name="allowUnconfirmed">Allow to spend unconfirmed transactions, if necessary.</param>
		public TransactionFactory(Network network, KeyManager keyManager, ICoinsView coins, string password = "", bool allowUnconfirmed = false)
		{
			Network = network;
			KeyManager = keyManager;
			Coins = coins;
			Password = password;
			AllowUnconfirmed = allowUnconfirmed;
		}

		/// <exception cref="ArgumentException"></exception>
		/// <exception cref="ArgumentNullException"></exception>
		/// <exception cref="ArgumentOutOfRangeException"></exception>
		public BuildTransactionResult BuildTransaction(
			PaymentIntent payments,
			FeeRate feeRate,
			IEnumerable<TxoRef> allowedInputs = null)
			=> BuildTransaction(payments, () => feeRate, allowedInputs, () => LockTime.Zero);

		/// <exception cref="ArgumentException"></exception>
		/// <exception cref="ArgumentNullException"></exception>
		/// <exception cref="ArgumentOutOfRangeException"></exception>
		public BuildTransactionResult BuildTransaction(
			PaymentIntent payments,
			Func<FeeRate> feeRateFetcher,
			IEnumerable<TxoRef> allowedInputs = null,
			Func<LockTime> lockTimeSelector = null)
		{
			payments = Guard.NotNull(nameof(payments), payments);
			lockTimeSelector ??= () => LockTime.Zero;

			long totalAmount = payments.TotalAmount.Satoshi;
			if (totalAmount < 0 || totalAmount > Constants.MaximumNumberOfSatoshis)
			{
				throw new ArgumentOutOfRangeException($"{nameof(payments)}.{nameof(payments.TotalAmount)} sum cannot be smaller than 0 or greater than {Constants.MaximumNumberOfSatoshis}.");
			}

			// Get allowed coins to spend.
			var availableCoinsView = Coins.Available();
			List<SmartCoin> allowedSmartCoinInputs = AllowUnconfirmed // Inputs that can be used to build the transaction.
					? availableCoinsView.ToList()
					: availableCoinsView.Confirmed().ToList();
			if (allowedInputs != null) // If allowedInputs are specified then select the coins from them.
			{
				if (!allowedInputs.Any())
				{
					throw new ArgumentException($"{nameof(allowedInputs)} is not null, but empty.");
				}

				allowedSmartCoinInputs = allowedSmartCoinInputs
					.Where(x => allowedInputs.Any(y => y.TransactionId == x.TransactionId && y.Index == x.Index))
					.ToList();

				// Add those that have the same script, because common ownership is already exposed.
				// But only if the user didn't click the "max" button. In this case he'd send more money than what he'd think.
				if (payments.ChangeStrategy != ChangeStrategy.AllRemainingCustom)
				{
					var allScripts = allowedSmartCoinInputs.Select(x => x.ScriptPubKey).ToHashSet();
					foreach (var coin in availableCoinsView.Where(x => !allowedSmartCoinInputs.Any(y => x.TransactionId == y.TransactionId && x.Index == y.Index)))
					{
						if (!(AllowUnconfirmed || coin.Confirmed))
						{
							continue;
						}

						if (allScripts.Contains(coin.ScriptPubKey))
						{
							allowedSmartCoinInputs.Add(coin);
						}
					}
				}
			}

			// Get and calculate fee
			Logger.LogInfo("Calculating dynamic transaction fee...");

			TransactionBuilder builder = Network.CreateTransactionBuilder();
			builder.SetCoinSelector(new SmartCoinSelector(allowedSmartCoinInputs));
			builder.AddCoins(allowedSmartCoinInputs.Select(c => c.GetCoin()));

			foreach (var request in payments.Requests.Where(x => x.Amount.Type == MoneyRequestType.Value))
			{
				var amountRequest = request.Amount;

				builder.Send(request.Destination, amountRequest.Amount);
				if (amountRequest.SubtractFee)
				{
					builder.SubtractFees();
				}
			}

			HdPubKey changeHdPubKey = null;

			if (payments.TryGetCustomRequest(out DestinationRequest custChange))
			{
				var changeScript = custChange.Destination.ScriptPubKey;
				changeHdPubKey = KeyManager.GetKeyForScriptPubKey(changeScript);

				var changeStrategy = payments.ChangeStrategy;
				if (changeStrategy == ChangeStrategy.Custom)
				{
					builder.SetChange(changeScript);
				}
				else if (changeStrategy == ChangeStrategy.AllRemainingCustom)
				{
					builder.SendAllRemaining(changeScript);
				}
				else
				{
					throw new NotSupportedException(payments.ChangeStrategy.ToString());
				}
			}
			else
			{
				KeyManager.AssertCleanKeysIndexed(isInternal: true);
				KeyManager.AssertLockedInternalKeysIndexed(14);
				changeHdPubKey = KeyManager.GetKeys(KeyState.Clean, true).RandomElement();

				builder.SetChange(changeHdPubKey.P2wpkhScript);
			}

			builder.OptInRBF = new Random().NextDouble() < Constants.TransactionRBFSignalRate;

			FeeRate feeRate = feeRateFetcher();
			builder.SendEstimatedFees(feeRate);

			var psbt = builder.BuildPSBT(false);

			var spentCoins = psbt.Inputs.Select(txin => allowedSmartCoinInputs.First(y => y.GetOutPoint() == txin.PrevOut)).ToArray();

			var realToSend = payments.Requests
				.Select(t =>
					(label: t.Label,
					destination: t.Destination,
					amount: psbt.Outputs.FirstOrDefault(o => o.ScriptPubKey == t.Destination.ScriptPubKey)?.Value))
				.Where(i => i.amount != null);

			if (!psbt.TryGetFee(out var fee))
			{
				throw new InvalidOperationException("Impossible to get the fees of the PSBT, this should never happen.");
			}
			Logger.LogInfo($"Fee: {fee.Satoshi} Satoshi.");

			var vSize = builder.EstimateSize(psbt.GetOriginalTransaction(), true);
			Logger.LogInfo($"Estimated tx size: {vSize} vBytes.");

			// Do some checks
			Money totalSendAmountNoFee = realToSend.Sum(x => x.amount);
			if (totalSendAmountNoFee == Money.Zero)
			{
				throw new InvalidOperationException("The amount after subtracting the fee is too small to be sent.");
			}
			Money totalSendAmount = totalSendAmountNoFee + fee;

			Money totalOutgoingAmountNoFee;
			if (changeHdPubKey is null)
			{
				totalOutgoingAmountNoFee = totalSendAmountNoFee;
			}
			else
			{
				totalOutgoingAmountNoFee = realToSend.Where(x => !changeHdPubKey.ContainsScript(x.destination.ScriptPubKey)).Sum(x => x.amount);
			}
			decimal totalOutgoingAmountNoFeeDecimal = totalOutgoingAmountNoFee.ToDecimal(MoneyUnit.BTC);
			// Cannot divide by zero, so use the closest number we have to zero.
			decimal totalOutgoingAmountNoFeeDecimalDivisor = totalOutgoingAmountNoFeeDecimal == 0 ? decimal.MinValue : totalOutgoingAmountNoFeeDecimal;
			decimal feePc = 100 * fee.ToDecimal(MoneyUnit.BTC) / totalOutgoingAmountNoFeeDecimalDivisor;

			if (feePc > 1)
			{
				Logger.LogInfo($"The transaction fee is {totalOutgoingAmountNoFee:0.#}% of your transaction amount.{Environment.NewLine}"
					+ $"Sending:\t {totalSendAmount.ToString(fplus: false, trimExcessZero: true)} BTC.{Environment.NewLine}"
					+ $"Fee:\t\t {fee.Satoshi} Satoshi.");
			}
			if (feePc > 100)
			{
				throw new InvalidOperationException($"The transaction fee is more than twice the transaction amount: {feePc:0.#}%.");
			}

			if (spentCoins.Any(u => !u.Confirmed))
			{
				Logger.LogInfo("Unconfirmed transaction is spent.");
			}

			// Build the transaction
			Logger.LogInfo("Signing transaction...");
			// It must be watch only, too, because if we have the key and also hardware wallet, we do not care we can sign.

			Transaction tx = null;
			if (KeyManager.IsWatchOnly)
			{
				tx = psbt.GetGlobalTransaction();
			}
			else
			{
				IEnumerable<ExtKey> signingKeys = KeyManager.GetSecrets(Password, spentCoins.Select(x => x.ScriptPubKey).ToArray());
				builder = builder.AddKeys(signingKeys.ToArray());
				builder.SetLockTime(lockTimeSelector());
				builder.SignPSBT(psbt);
				psbt.Finalize();
				tx = psbt.ExtractTransaction();

				var checkResults = builder.Check(tx).ToList();
				if (!psbt.TryGetEstimatedFeeRate(out FeeRate actualFeeRate))
				{
					throw new InvalidOperationException("Impossible to get the fee rate of the PSBT, this should never happen.");
				}

				// Manually check the feerate, because some inaccuracy is possible.
				var sb1 = feeRate.SatoshiPerByte;
				var sb2 = actualFeeRate.SatoshiPerByte;
				if (Math.Abs(sb1 - sb2) > 2) // 2s/b inaccuracy ok.
				{
					// So it'll generate a transactionpolicy error thrown below.
					checkResults.Add(new NotEnoughFundsPolicyError("Fees different than expected"));
				}
				if (checkResults.Count > 0)
				{
					throw new InvalidTxException(tx, checkResults);
				}
			}

			if (KeyManager.MasterFingerprint is HDFingerprint fp)
			{
				foreach (var coin in spentCoins)
				{
					var rootKeyPath = new RootedKeyPath(fp, coin.HdPubKey.FullKeyPath);
					psbt.AddKeyPath(coin.HdPubKey.PubKey, rootKeyPath, coin.ScriptPubKey);
				}
			}

			var label = SmartLabel.Merge(payments.Requests.Select(x => x.Label).Concat(spentCoins.Select(x => x.Label)));
			var outerWalletOutputs = new List<SmartCoin>();
			var innerWalletOutputs = new List<SmartCoin>();
			for (var i = 0U; i < tx.Outputs.Count; i++)
			{
				TxOut output = tx.Outputs[i];
				var anonset = tx.GetAnonymitySet(i) + spentCoins.Min(x => x.AnonymitySet) - 1; // Minus 1, because count own only once.
				var foundKey = KeyManager.GetKeyForScriptPubKey(output.ScriptPubKey);
				var coin = new SmartCoin(tx.GetHash(), i, output.ScriptPubKey, output.Value, tx.Inputs.ToTxoRefs().ToArray(), Height.Unknown, tx.RBF, anonset, pubKey: foundKey);
				label = SmartLabel.Merge(label, coin.Label); // foundKey's label is already added to the coinlabel.

				if (foundKey is null)
				{
					outerWalletOutputs.Add(coin);
				}
				else
				{
					innerWalletOutputs.Add(coin);
				}
			}

			foreach (var coin in outerWalletOutputs.Concat(innerWalletOutputs))
			{
				var foundPaymentRequest = payments.Requests.FirstOrDefault(x => x.Destination.ScriptPubKey == coin.ScriptPubKey);

				// If change then we concatenate all the labels.
				if (foundPaymentRequest is null) // Then it's autochange.
				{
					coin.Label = label;
				}
				else
				{
					coin.Label = SmartLabel.Merge(coin.Label, foundPaymentRequest.Label);
				}

				var foundKey = KeyManager.GetKeyForScriptPubKey(coin.ScriptPubKey);
				foundKey?.SetLabel(coin.Label); // The foundkeylabel has already been added previously, so no need to concatenate.
			}

			Logger.LogInfo($"Transaction is successfully built: {tx.GetHash()}.");
			var sign = !KeyManager.IsWatchOnly;
			var spendsUnconfirmed = spentCoins.Any(c => !c.Confirmed);
			return new BuildTransactionResult(new SmartTransaction(tx, Height.Unknown), psbt, spendsUnconfirmed, sign, fee, feePc, outerWalletOutputs, innerWalletOutputs, spentCoins);
		}
	}
}
