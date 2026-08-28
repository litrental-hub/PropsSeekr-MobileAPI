using System.Collections.Generic;

namespace propseekr_file_processor
{
    public class PropertyListing
    {
        // Canonical classifier. Prefer this over ListingType when present.
        // Allowed values: LISTING_SELL, LISTING_RENT, LISTING_LEASE,
        // REQ_BUY, REQ_RENT, REQ_LEASE, IGNORE.
        public string RecordKind { get; set; } = "";

        // Backward-compatible field used by older JSON/code:
        // Sale, Rent, Requirement.
        public string ListingType { get; set; } = "";

        public string SenderName { get; set; } = "";
        public string MessageDate { get; set; } = "";
        public string MessageDateTime { get; set; } = "";
        public string GroupName { get; set; } = "";

        public string PropertyType { get; set; } = "";
        public string Configuration { get; set; } = "";
        public string Location { get; set; } = "";
        public string ProjectName { get; set; } = "";

        public List<decimal>? Size { get; set; }
        public string SizeUnit { get; set; } = "";
        public decimal? Width { get; set; }
        public decimal? Length { get; set; }

        // Price is total sale price or monthly rent/budget only.
        public decimal? Price { get; set; }
        public string PriceUnit { get; set; } = "";

        // PricePerUnit is for PerSqFt, PerBigha, PerAcre only.
        public decimal? PricePerUnit { get; set; }

        public string Facing { get; set; } = "";
        public string RoadInfo { get; set; } = "";
        public string Furnishing { get; set; } = "";

        public string ContactName { get; set; } = "";
        public string ContactNumber { get; set; } = "";
        public string RawText { get; set; } = "";
    }
}

