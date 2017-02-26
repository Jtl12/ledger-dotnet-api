﻿using HidLibrary;
using LedgerWallet.Transports;
using Microsoft.Win32.SafeHandles;
using NBitcoin;
using NBitcoin.Crypto;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LedgerWallet
{
	public class LedgerClient : LedgerClientBase
	{
		public LedgerClient(ILedgerTransport transport) : base(transport)
		{
		}
		public static IEnumerable<LedgerClient> GetHIDLedgers()
		{
			var ledgers = HIDLedgerTransport.GetHIDTransports()
							.Select(t => new LedgerClient(t))
							.ToList();
			return ledgers;
		}

		public LedgerWalletFirmware GetFirmwareVersion()
		{
			using(Transport.Lock())
			{
				byte[] response = ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_FIRMWARE_VERSION, (byte)0x00, (byte)0x00, 0x00, OK);
				return new LedgerWalletFirmware(response);
			}
		}

		public GetWalletPubKeyResponse GetWalletPubKey(KeyPath keyPath)
		{
			using(Transport.Lock())
			{
				Guard.AssertKeyPath(keyPath);
				byte[] bytes = Serializer.Serialize(keyPath);
				//bytes[0] = 10;
				byte[] response = ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_WALLET_PUBLIC_KEY, (byte)0x00, (byte)0x00, bytes, OK);
				return new GetWalletPubKeyResponse(response);
			}
		}

		public TrustedInput GetTrustedInput(IndexedTxOut txout)
		{
			return GetTrustedInput(txout.Transaction, (int)txout.N);
		}
		public TrustedInput GetTrustedInput(Transaction transaction, int outputIndex)
		{
			using(Transport.Lock())
			{
				if(outputIndex >= transaction.Outputs.Count)
					throw new ArgumentOutOfRangeException("outputIndex is bigger than the number of outputs in the transaction", "outputIndex");
				MemoryStream data = new MemoryStream();
				// Header
				BufferUtils.WriteUint32BE(data, outputIndex);
				BufferUtils.WriteBuffer(data, transaction.Version);
				VarintUtils.write(data, transaction.Inputs.Count);
				ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_TRUSTED_INPUT, (byte)0x00, (byte)0x00, data.ToArray(), OK);
				// Each input
				foreach(var input in transaction.Inputs)
				{
					data = new MemoryStream();
					BufferUtils.WriteBuffer(data, input.PrevOut);
					VarintUtils.write(data, input.ScriptSig.Length);
					ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_TRUSTED_INPUT, (byte)0x80, (byte)0x00, data.ToArray(), OK);
					data = new MemoryStream();
					BufferUtils.WriteBuffer(data, input.ScriptSig.ToBytes());
					ExchangeApduSplit2(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_TRUSTED_INPUT, (byte)0x80, (byte)0x00, data.ToArray(), Utils.ToBytes(input.Sequence, true), OK);
				}
				// Number of outputs
				data = new MemoryStream();
				VarintUtils.write(data, transaction.Outputs.Count);
				ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_TRUSTED_INPUT, (byte)0x80, (byte)0x00, data.ToArray(), OK);
				// Each output
				foreach(var output in transaction.Outputs)
				{
					data = new MemoryStream();
					BufferUtils.WriteBuffer(data, Utils.ToBytes((ulong)output.Value.Satoshi, true));
					VarintUtils.write(data, output.ScriptPubKey.Length);
					ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_TRUSTED_INPUT, (byte)0x80, (byte)0x00, data.ToArray(), OK);
					data = new MemoryStream();
					BufferUtils.WriteBuffer(data, output.ScriptPubKey.ToBytes());
					ExchangeApduSplit(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_TRUSTED_INPUT, (byte)0x80, (byte)0x00, data.ToArray(), OK);
				}
				// Locktime
				byte[] response = ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_GET_TRUSTED_INPUT, (byte)0x80, (byte)0x00, transaction.LockTime.ToBytes(), OK);
				return new TrustedInput(response);
			}
		}

		public void UntrustedHashTransactionInputStart(bool newTransaction, Transaction tx, int index, TrustedInput[] trustedInputs)
		{
			UntrustedHashTransactionInputStart(newTransaction, tx.Inputs.AsIndexedInputs().Skip(index).First(), trustedInputs);
		}
		public void UntrustedHashTransactionInputStart(bool newTransaction, IndexedTxIn txIn, TrustedInput[] trustedInputs)
		{
			using(Transport.Lock())
			{
				trustedInputs = trustedInputs ?? new TrustedInput[0];
				// Start building a fake transaction with the passed inputs
				MemoryStream data = new MemoryStream();
				BufferUtils.WriteBuffer(data, txIn.Transaction.Version);
				VarintUtils.write(data, txIn.Transaction.Inputs.Count);
				ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_HASH_INPUT_START, (byte)0x00, (newTransaction ? (byte)0x00 : (byte)0x80), data.ToArray(), OK);
				// Loop for each input
				long currentIndex = 0;
				foreach(var input in txIn.Transaction.Inputs)
				{
					var trustedInput = trustedInputs.FirstOrDefault(i => i.OutPoint == input.PrevOut);
					byte[] script = (currentIndex == txIn.Index ? txIn.TxIn.ScriptSig.ToBytes() : new byte[0]);
					data = new MemoryStream();
					if(trustedInput != null)
					{
						data.WriteByte(0x01);
						var b = trustedInput.ToBytes();
						// untrusted inputs have constant length
						data.WriteByte((byte)b.Length);
						BufferUtils.WriteBuffer(data, b);
					}
					else
					{
						data.WriteByte(0x00);
						BufferUtils.WriteBuffer(data, input.PrevOut);
					}
					VarintUtils.write(data, script.Length);
					ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_HASH_INPUT_START, (byte)0x80, (byte)0x00, data.ToArray(), OK);
					data = new MemoryStream();
					BufferUtils.WriteBuffer(data, script);
					BufferUtils.WriteBuffer(data, input.Sequence);
					ExchangeApduSplit(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_HASH_INPUT_START, (byte)0x80, (byte)0x00, data.ToArray(), OK);
					currentIndex++;
				}
			}
		}

		public byte[] UntrustedHashTransactionInputFinalizeFull(KeyPath change, IEnumerable<TxOut> outputs)
		{
			using(Transport.Lock())
			{
				if(change != null)
				{
					ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_HASH_INPUT_FINALIZE_FULL, 0xFF, 0x00, Serializer.Serialize(change), OK);
				}

				byte[] result = null;
				int offset = 0;
				byte[] response = null;
				var ms = new MemoryStream();
				BitcoinStream bs = new BitcoinStream(ms, true);
				var list = outputs.ToList();
				bs.ReadWrite<List<TxOut>, TxOut>(ref list);
				var data = ms.ToArray();

				while(offset < data.Length)
				{
					int blockLength = ((data.Length - offset) > 255 ? 255 : data.Length - offset);
					byte[] apdu = new byte[blockLength + 5];
					apdu[0] = LedgerWalletConstants.LedgerWallet_CLA;
					apdu[1] = LedgerWalletConstants.LedgerWallet_INS_HASH_INPUT_FINALIZE_FULL;
					apdu[2] = ((offset + blockLength) == data.Length ? (byte)0x80 : (byte)0x00);
					apdu[3] = (byte)0x00;
					apdu[4] = (byte)(blockLength);
					Array.Copy(data, offset, apdu, 5, blockLength);
					response = ExchangeApdu(apdu, OK);
					offset += blockLength;
				}
				result = response; //convertResponseToOutput(response);
				if(result == null)
				{
					throw new LedgerWalletException("Unsupported user confirmation method");
				}
				return result;
			}
		}

		public Transaction SignTransaction(KeyPath keyPath, ICoin[] signedCoins, Transaction[] parents, Transaction transaction, KeyPath changePath = null)
		{
			List<SignatureRequest> requests = new List<SignatureRequest>();
			foreach(var c in signedCoins)
			{
				var tx = parents.FirstOrDefault(t => t.GetHash() == c.Outpoint.Hash);
				if(tx != null)
					requests.Add(new SignatureRequest()
					{
						InputCoin = c,
						InputTransaction = tx,
						KeyPath = keyPath
					});
			}
			return SignTransaction(requests.ToArray(), transaction, changePath: changePath);
		}

		public Transaction SignTransaction(SignatureRequest[] signatureRequests, Transaction transaction, KeyPath changePath = null, bool verify = true)
		{
			List<TrustedInput> trustedInputs = new List<TrustedInput>();
			Dictionary<uint256, Transaction> knownTransactions = signatureRequests
				.Select(o => o.InputTransaction)
				.ToDictionaryUnique(o => o.GetHash());
			Dictionary<OutPoint, SignatureRequest> requests = signatureRequests
				.ToDictionaryUnique(o => o.InputCoin.Outpoint);
			foreach(var input in transaction.Inputs)
			{
				Transaction parent;
				if(!knownTransactions.TryGetValue(input.PrevOut.Hash, out parent))
					throw new KeyNotFoundException("Parent transaction " + input.PrevOut.Hash + " not found");
				trustedInputs.Add(GetTrustedInput(parent, (int)input.PrevOut.N));
			}
			using(Transport.Lock())
			{
				var inputs = trustedInputs.ToArray();

				transaction = transaction.Clone();
				
				bool newTransaction = true;
				foreach(var input in transaction.Inputs.AsIndexedInputs())
				{
					SignatureRequest previousReq;
					if(requests.TryGetValue(input.PrevOut, out previousReq))
						input.ScriptSig = previousReq.InputCoin.GetScriptCode();

					UntrustedHashTransactionInputStart(newTransaction, input, inputs);
					newTransaction = false;

					UntrustedHashTransactionInputFinalizeFull(changePath, transaction.Outputs);

					var sig = UntrustedHashSign(previousReq.KeyPath, null, transaction.LockTime, SigHash.All);

					var pubkey = previousReq.PubKey ?? GetWalletPubKey(previousReq.KeyPath).UncompressedPublicKey.Compress();
					input.ScriptSig =
						pubkey.ScriptPubKey == previousReq.InputCoin.TxOut.ScriptPubKey ?
							PayToPubkeyTemplate.Instance.GenerateScriptSig(sig) :
						pubkey.Hash.ScriptPubKey == previousReq.InputCoin.TxOut.ScriptPubKey ?
						PayToPubkeyHashTemplate.Instance.GenerateScriptSig(sig, pubkey) : null;
					if(input.ScriptSig == null)
						throw new InvalidOperationException("Impossible to generate the scriptSig of this transaction");
					if(verify)
					{
						ScriptError error;
						if(!Script.VerifyScript(previousReq.InputCoin.TxOut.ScriptPubKey, transaction, (int)input.Index, previousReq.InputCoin.TxOut.Value, out error))
							return null;
					}
				}

				return transaction;
			}
		}

		public TransactionSignature UntrustedHashSign(KeyPath keyPath, UserPin pin, LockTime lockTime, SigHash sigHashType)
		{
			using(Transport.Lock())
			{
				MemoryStream data = new MemoryStream();
				byte[] path = Serializer.Serialize(keyPath);
				BufferUtils.WriteBuffer(data, path);

				var pinBytes = pin == null ? new byte[0] : pin.ToBytes();
				data.WriteByte((byte)pinBytes.Length);
				BufferUtils.WriteBuffer(data, pinBytes);
				BufferUtils.WriteUint32BE(data, (uint)lockTime);
				data.WriteByte((byte)sigHashType);
				byte[] response = ExchangeApdu(LedgerWalletConstants.LedgerWallet_CLA, LedgerWalletConstants.LedgerWallet_INS_HASH_SIGN, (byte)0x00, (byte)0x00, data.ToArray(), OK);
				response[0] = (byte)0x30;
				return new TransactionSignature(response);
			}
		}
	}

	[Flags]
	public enum FirmwareFeatures : byte
	{
		Compressed = 0x01,
		SecureElementUI = 0x02,
		ExternalUI = 0x04,
		NFC = 0x08,
		BLE = 0x10,
		TrustedEnvironmentExecution = 0x20
	}


	//https://ledgerhq.github.io/LedgerWallet-doc/bitcoin-technical.html#_get_firmware_version
	public class LedgerWalletFirmware
	{
		public LedgerWalletFirmware(int major, int minor, int patch, bool compressedKeys)
		{

		}

		public LedgerWalletFirmware(byte[] bytes)
		{
			_Features = (FirmwareFeatures)(bytes[0] & ~0xC0);
			_Architecture = bytes[1];
			_Major = bytes[2];
			_Minor = bytes[3];
			_Patch = bytes[4];
			_LoaderMinor = bytes[5];
			_LoaderMajor = bytes[6];
		}

		private readonly FirmwareFeatures _Features;
		public FirmwareFeatures Features
		{
			get
			{
				return _Features;
			}
		}

		private readonly byte _Architecture;
		public byte Architecture
		{
			get
			{
				return _Architecture;
			}
		}

		private readonly byte _Major;
		public byte Major
		{
			get
			{
				return _Major;
			}
		}

		private readonly byte _Minor;
		public byte Minor
		{
			get
			{
				return _Minor;
			}
		}

		private readonly byte _Patch;
		public byte Patch
		{
			get
			{
				return _Patch;
			}
		}


		private readonly byte _LoaderMajor;
		public byte LoaderMajor
		{
			get
			{
				return _LoaderMajor;
			}
		}

		private readonly byte _LoaderMinor;
		public byte LoaderMinor
		{
			get
			{
				return _LoaderMinor;
			}
		}

		public override string ToString()
		{
			return (Architecture != 0 ? "Ledger " : "") + string.Format("{0}.{1}.{2} (Loader : {3}.{4})", Major, Minor, Patch, LoaderMajor, LoaderMinor);
		}
	}
}
