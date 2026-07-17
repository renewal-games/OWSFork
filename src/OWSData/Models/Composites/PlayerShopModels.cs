using System;
using System.Collections.Generic;
using OWSData.Models.StoredProcs;

namespace OWSData.Models.Composites
{
    // ---- Inputs ----

    public class PlayerShopListingInput
    {
        public int ListingIndex { get; set; }
        public string ItemIDTag { get; set; }
        public int Quantity { get; set; }
        public int UnitPriceGold { get; set; }
        public int TaxRateBps { get; set; }
        public string CustomData { get; set; }
    }

    public class CreateShopInput
    {
        public Guid OperationId { get; set; }
        public int OwnerCharacterID { get; set; }
        public string ShopName { get; set; }
        public string ZoneName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double RX { get; set; }
        public double RY { get; set; }
        public double RZ { get; set; }
        public string AppearanceJson { get; set; }
        public long ExpectedRevision { get; set; }
        public int OpeningFeeGold { get; set; }
        public double MinShopSpacingUnits { get; set; }
        public long ReopenCooldownSeconds { get; set; }
        public int PostEscrowGold { get; set; }
        public List<PlayerShopListingInput> Listings { get; set; } = new List<PlayerShopListingInput>();
        public List<UpdateCharacterInventory> PostEscrowInventory { get; set; } = new List<UpdateCharacterInventory>();
    }

    public class PurchaseInput
    {
        public Guid OperationId { get; set; }
        public long PlayerShopID { get; set; }
        public long PlayerShopItemID { get; set; }
        public int Quantity { get; set; }
        public int BuyerCharacterID { get; set; }
        public int ExpectedUnitPrice { get; set; }
        public long BuyerExpectedRevision { get; set; }
        public int MerchantXpPerGoldBps { get; set; } // XP = floor(total * bps / 10000); 10000 = 1xp/gold
    }

    public class ConfirmDeliveryInput
    {
        public Guid OperationId { get; set; }
        public int BuyerCharacterID { get; set; }
        public long ExpectedRevision { get; set; }
        public List<UpdateCharacterInventory> PostDeliveryInventory { get; set; } = new List<UpdateCharacterInventory>();
    }

    public class ClaimItemInput
    {
        public long PlayerShopItemID { get; set; }
        public int QuantityToReturn { get; set; }
    }

    public class ClaimEscrowInput
    {
        public Guid OperationId { get; set; }
        public long PlayerShopID { get; set; }
        public int OwnerCharacterID { get; set; }
        public bool ClaimGold { get; set; }
        public bool ClaimXp { get; set; }
        public long ExpectedRevision { get; set; }
        public int PostClaimGold { get; set; }
        public List<ClaimItemInput> ClaimedItems { get; set; } = new List<ClaimItemInput>();
        public List<UpdateCharacterInventory> PostClaimInventory { get; set; } = new List<UpdateCharacterInventory>();
    }

    // ---- Outputs ----

    public class PlayerShopResult
    {
        public bool Success { get; set; }
        public string ReasonCode { get; set; } = "";
        public long PlayerShopID { get; set; }
        public long NewRevision { get; set; }
    }

    public class PurchaseResult
    {
        public bool Success { get; set; }
        public string ReasonCode { get; set; } = "";
        public string ItemIDTag { get; set; } = "";
        public string CustomData { get; set; }
        public int QuantityGranted { get; set; }
        public int RemainingQuantity { get; set; }
        public bool ShopSoldOut { get; set; }
        public long NewBuyerRevision { get; set; }
        public int TaxPaid { get; set; }
        public int ProceedsCredited { get; set; }
        public int XpCredited { get; set; }
    }

    public class PlayerShopListingView
    {
        public long PlayerShopItemID { get; set; }
        public int ListingIndex { get; set; }
        public string ItemIDTag { get; set; }
        public int QuantityRemainingForSale { get; set; }
        public int UnitPriceGold { get; set; }
        public int TaxRateBps { get; set; }
        public string CustomData { get; set; }
    }

    public class PlayerShopView
    {
        public long PlayerShopID { get; set; }
        public string ShopName { get; set; }
        public string OwnerCharName { get; set; }
        public int OwnerCharacterID { get; set; }
        public string ZoneName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double RX { get; set; }
        public double RY { get; set; }
        public double RZ { get; set; }
        public string SaleStatus { get; set; }
        public string AppearanceJson { get; set; }
        public List<PlayerShopListingView> Listings { get; set; } = new List<PlayerShopListingView>();
    }

    public class PendingDeliveryView
    {
        public Guid OperationId { get; set; }
        public long PlayerShopID { get; set; }
        public long PlayerShopItemID { get; set; }
        public string ItemIDTag { get; set; }
        public string CustomData { get; set; }
        public int Quantity { get; set; }
        public int UnitPriceGold { get; set; }
        public int TaxPaid { get; set; }
    }

    public class PendingClaimItemView
    {
        public long PlayerShopItemID { get; set; }
        public int ListingIndex { get; set; }
        public string ItemIDTag { get; set; }
        public int QuantityPendingReturn { get; set; }
        public string CustomData { get; set; }
    }

    public class PendingClaimView
    {
        public long PlayerShopID { get; set; }
        public string ShopName { get; set; }
        public string SaleStatus { get; set; }
        public string SettlementStatus { get; set; }
        public int ProceedsGoldPending { get; set; }
        public int MerchantXpPending { get; set; }
        public List<PendingClaimItemView> Items { get; set; } = new List<PendingClaimItemView>();
    }

    public class ClaimEscrowResult
    {
        public bool Success { get; set; }
        public string ReasonCode { get; set; } = "";
        public long NewRevision { get; set; }
        public int GoldClaimed { get; set; }
        public int XpClaimed { get; set; }
        public string SettlementStatus { get; set; } = "";
    }

    public class ResolveUndeliveredResult
    {
        public bool Success { get; set; }
        public string ReasonCode { get; set; } = "";
        public long NewBuyerRevision { get; set; }
        public int GoldRefunded { get; set; }
    }

    // Replay envelope for GetOperationResult.
    public class OperationResultView
    {
        public bool Found { get; set; }
        public string OpType { get; set; } = "";
        public string State { get; set; } = "";
        public string ResultJson { get; set; } = "";
    }
}
