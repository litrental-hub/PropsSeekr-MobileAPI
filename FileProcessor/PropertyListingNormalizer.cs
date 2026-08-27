using System;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using Amazon.Lambda.Core;

namespace propseekr_file_processor
{
    public static class PropertyListingNormalizer
    {
        public static string NormalizeRecordKind(string? recordKind, string? listingType, string? rawText)
        {
            var rk = NormalizeToken(recordKind);
            if (IsKnownRecordKind(rk)) return rk;

            var legacy = (listingType ?? "").Trim().ToLowerInvariant();
            var text = NormalizeIntentText($"{rawText} {listingType}");

            var hasLeaseIntent = Regex.IsMatch(text, @"\b(lease|leased)\b", RegexOptions.IgnoreCase);
            var hasRentIntent = Regex.IsMatch(text,
                @"\b(rent|rental|kiraya|kiraaya|kiraaye|kirae|per\s+month|monthly|mahina|mahine)\b",
                RegexOptions.IgnoreCase);
            var hasSaleIntent = Regex.IsMatch(text,
                @"\b(sale|sell|for\s+sale|available|avl|rate|demand|price|project|jv|joint\s+venture|investment\s+opportunity|builder\s+opportunity|developer\s+opportunity)\b",
                RegexOptions.IgnoreCase);
            var hasSupplyIntent = Regex.IsMatch(text,
                @"\b(available|available\s+units|for\s+rent|for\s+sale|available\s+for\s+rent|available\s+on\s+rent|flat\s+for\s+rent|house\s+for\s+rent|bungalow\s+for\s+rent|office\s+for\s+rent|need\s+to\s+rent\s+out|rent\s+out|renting\s+out|need\s+to\s+sell|urgently\s+sell|sell\s+urgent|rent\s+enquiry|preferred\s+tenant|suitable\s+for|daily\s+need\s+shops)\b",
                RegexOptions.IgnoreCase);
            var hasRequirementIntent = IsRequirementIntent(text, hasSupplyIntent);

            if (hasRequirementIntent || legacy == "requirement" || legacy == "req")
            {
                if (hasLeaseIntent) return "REQ_LEASE";
                if (hasRentIntent || legacy == "rent" || legacy == "rental") return "REQ_RENT";
                return "REQ_BUY";
            }

            if (hasLeaseIntent || legacy == "lease") return "LISTING_LEASE";
            if (hasRentIntent || legacy == "rent" || legacy == "rental") return "LISTING_RENT";
            if (hasSaleIntent || legacy == "sale" || legacy == "sell") return "LISTING_SELL";

            return "";
        }

        private static bool IsRequirementIntent(string text, bool hasSupplyIntent)
        {
            if (Regex.IsMatch(text,
                    @"\b(client\s+required|urgent\s+required|urgently\s+required|requirement|req\.?|wanted|looking\s+for|chahiye|chaiye|lena\s+hai|len[aey]|kharidna|purchase\s+required|buyer\s+required|buyer\s+hai)\b",
                    RegexOptions.IgnoreCase))
                return true;

            if (Regex.IsMatch(text,
                    @"\b(?:need|needed|want|required)\s+(?:\d+\s*bhk|rk|flat|house|duplex|bungalow|villa|plot|land|office|shop|showroom|godown|warehouse|commercial)\b",
                    RegexOptions.IgnoreCase))
                return !hasSupplyIntent;

            if (Regex.IsMatch(text,
                    @"\b(?:\d+\s*bhk|rk|flat|house|duplex|bungalow|villa|plot|land|office|shop|showroom|godown|warehouse|commercial).{0,60}\b(?:required|wanted|needed|need|chahiye|chaiye)\b",
                    RegexOptions.IgnoreCase))
                return !hasSupplyIntent;

            return false;
        }

        public static PropertyListing NormalizeCanonicalFields(this PropertyListing listing)
        {
            if (listing == null) throw new ArgumentNullException(nameof(listing));

            listing.RecordKind = NormalizeRecordKind(listing.RecordKind, listing.ListingType, listing.RawText);
            listing.ListingType = LegacyListingTypeFromRecordKind(listing.RecordKind, listing.ListingType);
            listing.Configuration = NormalizeConfiguration(listing.Configuration, listing.RawText);
            listing.PriceUnit = NormalizePriceUnit(listing.PriceUnit);

            // Sanitize all text fields from Hindi/Unicode garbage to clean up the final JSON output
            listing.Location = SanitizeStringField(listing.Location);
            listing.ProjectName = SanitizeStringField(listing.ProjectName);
            listing.RoadInfo = SanitizeStringField(listing.RoadInfo);
            listing.ContactName = SanitizeStringField(listing.ContactName);
            listing.Facing = SanitizeStringField(listing.Facing);
            listing.SenderName = SanitizeStringField(listing.SenderName);

            BackfillMissingPriceFromRawText(listing);
            NormalizePerUnitPrice(listing);
            NormalizeLoosePropertyType(listing);

            // Clear configuration for land and commercial properties
            if (!string.IsNullOrWhiteSpace(listing.PropertyType))
            {
                var pt = listing.PropertyType.Trim().ToLowerInvariant();
                if (pt.Contains("plot") || pt.Contains("land") || pt.Contains("shop") || 
                    pt.Contains("office") || pt.Contains("showroom") || pt.Contains("warehouse") || 
                    pt.Contains("godown") || pt.Contains("commercial") || pt.Contains("industrial") || 
                    pt.Contains("factory") || pt.Contains("school") || pt.Contains("college") || 
                    pt.Contains("hospital") || pt.Contains("clinic") || pt.Contains("hotel") || 
                    pt.Contains("guest house"))
                {
                    listing.Configuration = "";
                }
            }

            // Sanitize RawText at the very end so that normalizers can use the original content first
            // PREVIOUS CODE / BACKUP: listing.RawText = SanitizeStringField(listing.RawText);

            return listing;
        }

        public static string LegacyListingTypeFromRecordKind(string? recordKind, string? fallbackListingType)
        {
            var rk = NormalizeToken(recordKind);
            if (rk == "LISTING_SELL") return "Sale";
            if (rk == "LISTING_RENT") return "Rent";
            if (rk == "LISTING_LEASE") return "Lease";
            if (rk == "REQ_BUY" || rk == "REQ_RENT" || rk == "REQ_LEASE") return "Requirement";
            return fallbackListingType ?? "";
        }

        public static string NormalizePriceUnit(string? unit)
        {
            var u = (unit ?? "").Trim();
            if (u.Length == 0) return "";

            var key = u.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
            if (key == "total" || key == "lumpsum" || key == "lumpsumtotal") return "Total";
            if (key == "permonth" || key == "monthly" || key == "month") return "PerMonth";
            if (key == "persqft" || key == "sqft" || key == "persquarefeet" || key == "persquarefoot") return "PerSqFt";
            if (key == "perbigha" || key == "bigha") return "PerBigha";
            if (key == "peracre" || key == "acre") return "PerAcre";
            if (key == "persqyard" || key == "sqyard" || key == "peryard") return "PerSqFt";
            return u;
        }

        /* PREVIOUS CODE / BACKUP:
        public static string NormalizeConfiguration(string? configuration, string? rawText)
        {
            var source = !string.IsNullOrWhiteSpace(configuration)
                ? configuration
                : rawText;
            if (string.IsNullOrWhiteSpace(source)) return "";

            var text = NormalizeIntentText(source);

            var bhk = Regex.Match(text, @"\b(\d+(?:\.\d+)?)\s*b\s*h\s*k\b", RegexOptions.IgnoreCase);
            if (bhk.Success)
                return $"{bhk.Groups[1].Value.ToUpperInvariant()}BHK";

            var compactBhk = Regex.Match(text, @"\b(\d+(?:\.\d+)?)bhk\b", RegexOptions.IgnoreCase);
            if (compactBhk.Success)
                return $"{compactBhk.Groups[1].Value.ToUpperInvariant()}BHK";

            var rk = Regex.Match(text, @"\b(\d+)?\s*r\s*k\b", RegexOptions.IgnoreCase);
            if (rk.Success)
            {
                var rooms = string.IsNullOrWhiteSpace(rk.Groups[1].Value) ? "1" : rk.Groups[1].Value;
                return $"{rooms}RK";
            }

            return configuration?.Trim().ToUpperInvariant().Replace(" ", "") ?? "";
        }
        */

        public static string NormalizeConfiguration(string? configuration, string? rawText)
        {
            if (!string.IsNullOrWhiteSpace(configuration))
            {
                var parts = configuration.Split(new[] { ',', '/', '&', '-', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var normalizedParts = new List<string>();
                foreach (var part in parts)
                {
                    var trimmed = part.Trim().ToUpperInvariant().Replace(" ", "");
                    if (Regex.IsMatch(trimmed, @"^\d+(?:\.\d+)?(?:BHK|RK)$"))
                    {
                        normalizedParts.Add(trimmed);
                    }
                    else
                    {
                        var matchBhk = Regex.Match(part, @"\b(\d+(?:\.\d+)?)\s*(?:b\s*h\s*k|bhk)\b", RegexOptions.IgnoreCase);
                        if (matchBhk.Success)
                        {
                            normalizedParts.Add($"{matchBhk.Groups[1].Value.ToUpperInvariant()}BHK");
                        }
                        else
                        {
                            var matchRk = Regex.Match(part, @"\b(\d+)?\s*(?:r\s*k|rk)\b", RegexOptions.IgnoreCase);
                            if (matchRk.Success)
                            {
                                var rooms = string.IsNullOrWhiteSpace(matchRk.Groups[1].Value) ? "1" : matchRk.Groups[1].Value;
                                normalizedParts.Add($"{rooms}RK");
                            }
                        }
                    }
                }
                if (normalizedParts.Count > 0)
                {
                    return string.Join(", ", normalizedParts);
                }
            }

            if (string.IsNullOrWhiteSpace(rawText)) return "";
            var text = NormalizeIntentText(rawText);

            // Handle multi-BHK ranges like "2, 3 BHK" or "2-3 bhk" or "2 and 3 bhk"
            var rangeMatch = Regex.Match(text, @"\b(\d+)(?:\s*(?:,|\s+and|or|-)\s*(\d+))?\s*(?:b\s*h\s*k|bhk)\b", RegexOptions.IgnoreCase);
            if (rangeMatch.Success)
            {
                var list = new List<string>();
                list.Add($"{rangeMatch.Groups[1].Value}BHK");
                if (rangeMatch.Groups[2].Success)
                {
                    list.Add($"{rangeMatch.Groups[2].Value}BHK");
                }
                return string.Join(", ", list);
            }

            var allBhkMatches = Regex.Matches(text, @"\b(\d+(?:\.\d+)?)\s*(?:b\s*h\s*k|bhk)\b", RegexOptions.IgnoreCase);
            var allRkMatches = Regex.Matches(text, @"\b(\d+)?\s*(?:r\s*k|rk)\b", RegexOptions.IgnoreCase);

            var extracted = new List<string>();
            foreach (Match m in allBhkMatches)
            {
                var val = $"{m.Groups[1].Value.ToUpperInvariant()}BHK";
                if (!extracted.Contains(val)) extracted.Add(val);
            }
            foreach (Match m in allRkMatches)
            {
                var rooms = string.IsNullOrWhiteSpace(m.Groups[1].Value) ? "1" : m.Groups[1].Value;
                var val = $"{rooms}RK";
                if (!extracted.Contains(val)) extracted.Add(val);
            }

            if (extracted.Count > 0)
            {
                return string.Join(", ", extracted);
            }

            return configuration?.Trim().ToUpperInvariant().Replace(" ", "") ?? "";
        }

        private static void BackfillMissingPriceFromRawText(PropertyListing listing)
        {
            if (listing.Price.HasValue || listing.PricePerUnit.HasValue)
                return;

            var raw = NormalizeIntentText(listing.RawText);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            var perBigha = Regex.Match(raw,
                @"(?<!\d)(\d+(?:\.\d+)?)\s*(lakh|lac|cr|crore)\s*(?:per|/)?\s*bigha\b",
                RegexOptions.IgnoreCase);
            if (perBigha.Success && TryParseDecimal(perBigha.Groups[1].Value, out var bighaValue))
            {
                listing.PricePerUnit = ExpandIndianMoney(bighaValue, perBigha.Groups[2].Value);
                listing.PriceUnit = "PerBigha";
                return;
            }

            var total = Regex.Match(raw,
                @"\b(?:demand|price|asking|ask|rate)\s*[:\-]?\s*(?:rs|inr)?\s*(\d+(?:\.\d+)?)\s*(cr|crore|lakh|lac)\b",
                RegexOptions.IgnoreCase);
            if (total.Success && TryParseDecimal(total.Groups[1].Value, out var totalValue))
            {
                listing.Price = ExpandIndianMoney(totalValue, total.Groups[2].Value);
                listing.PriceUnit = "Total";
                return;
            }

            var hasSqftContext = Regex.IsMatch(raw,
                @"\b(sqft|sq\.?\s*ft|square\s*feet|sft|sf|plot\s*size|size)\b",
                RegexOptions.IgnoreCase);
            var isPlotLike = Regex.IsMatch($"{listing.PropertyType} {raw}",
                @"\b(plot|land|commercial)\b",
                RegexOptions.IgnoreCase);
            var rate = Regex.Match(raw,
                @"\b(?:rate|demand|asking\s*rate|net\s*rate|@)\s*[:\-]?\s*(?:rs|inr)?\s*(\d{3,6}(?:\.\d+)?)\s*(?:/-)?\s*(?:rs|rupay|rupees)?\s*(?:per\s*)?(?:sqft|sq\.?\s*ft|square\s*feet|sft|sf)?\b",
                RegexOptions.IgnoreCase);
            if (rate.Success
                && hasSqftContext
                && isPlotLike
                && !Regex.IsMatch(rate.Value, @"\b(lakh|lac|cr|crore)\b", RegexOptions.IgnoreCase)
                && TryParseDecimal(rate.Groups[1].Value, out var rateValue)
                && rateValue >= 100m
                && rateValue <= 100000m)
            {
                listing.PricePerUnit = rateValue;
                listing.PriceUnit = "PerSqFt";
            }
        }

        private static bool TryParseDecimal(string text, out decimal value)
        {
            return decimal.TryParse(text.Replace(",", ""), NumberStyles.Number,
                CultureInfo.InvariantCulture, out value);
        }

        private static decimal ExpandIndianMoney(decimal value, string unit)
        {
            var u = (unit ?? "").Trim().ToLowerInvariant();
            if (u == "cr" || u == "crore") return value * 10000000m;
            if (u == "lakh" || u == "lac") return value * 100000m;
            return value;
        }

        private static void NormalizePerUnitPrice(PropertyListing listing)
        {
            var unit = NormalizePriceUnit(listing.PriceUnit);
            var raw = NormalizeIntentText(listing.RawText);

            var isPerUnit = unit == "PerSqFt"
                || unit == "PerBigha"
                || unit == "PerAcre";

            if (isPerUnit && listing.Price.HasValue && !listing.PricePerUnit.HasValue)
            {
                listing.PricePerUnit = listing.Price;
                listing.Price = null;
            }

            if (string.IsNullOrWhiteSpace(unit) && listing.Price.HasValue && !listing.PricePerUnit.HasValue)
            {
                if (Regex.IsMatch(raw, @"\b(per\s*sq\s*ft|per\s*sqft|sqft|square\s*feet|sq\.?\s*ft\.?)\b", RegexOptions.IgnoreCase))
                {
                    listing.PricePerUnit = listing.Price;
                    listing.Price = null;
                    listing.PriceUnit = "PerSqFt";
                }
                else if (Regex.IsMatch(raw, @"\b(per\s*bigha|bigha)\b", RegexOptions.IgnoreCase))
                {
                    listing.PricePerUnit = listing.Price;
                    listing.Price = null;
                    listing.PriceUnit = "PerBigha";
                }
                else if (Regex.IsMatch(raw, @"\b(per\s*acre|acre)\b", RegexOptions.IgnoreCase))
                {
                    listing.PricePerUnit = listing.Price;
                    listing.Price = null;
                    listing.PriceUnit = "PerAcre";
                }
            }

            if (listing.PriceUnit == "PerMonth" && listing.PricePerUnit.HasValue && !listing.Price.HasValue)
            {
                listing.Price = listing.PricePerUnit;
                listing.PricePerUnit = null;
            }
        }

        private static void NormalizeLoosePropertyType(PropertyListing listing)
        {
            var type = (listing.PropertyType ?? "").Trim();
            if (type.Equals("Residential", StringComparison.OrdinalIgnoreCase)
                || type.Equals("Commercial Property", StringComparison.OrdinalIgnoreCase)
                || type.Equals("Property", StringComparison.OrdinalIgnoreCase))
            {
                listing.PropertyType = InferPropertyType(listing.RawText);
            }
        }

        private static string InferPropertyType(string? rawText)
        {
            var text = NormalizeIntentText(rawText);
            if (Regex.IsMatch(text, @"\b(flat|apartment|bhk)\b", RegexOptions.IgnoreCase)) return "Flat";
            if (Regex.IsMatch(text, @"\b(plot|plots)\b", RegexOptions.IgnoreCase)) return "Plot";
            if (Regex.IsMatch(text, @"\b(land|jameen|zameen|farm)\b", RegexOptions.IgnoreCase)) return "Land";
            if (Regex.IsMatch(text, @"\b(office)\b", RegexOptions.IgnoreCase)) return "Office";
            if (Regex.IsMatch(text, @"\b(shop)\b", RegexOptions.IgnoreCase)) return "Shop";
            if (Regex.IsMatch(text, @"\b(showroom)\b", RegexOptions.IgnoreCase)) return "Showroom";
            if (Regex.IsMatch(text, @"\b(duplex)\b", RegexOptions.IgnoreCase)) return "Duplex";
            if (Regex.IsMatch(text, @"\b(villa)\b", RegexOptions.IgnoreCase)) return "Villa";
            if (Regex.IsMatch(text, @"\b(house|bungalow|banglow)\b", RegexOptions.IgnoreCase)) return "House";
            return "";
        }

        private static bool IsKnownRecordKind(string token)
        {
            return token == "LISTING_SELL"
                || token == "LISTING_RENT"
                || token == "LISTING_LEASE"
                || token == "REQ_BUY"
                || token == "REQ_RENT"
                || token == "REQ_LEASE"
                || token == "IGNORE";
        }

        private static string NormalizeToken(string? value)
        {
            return (value ?? "").Trim().ToUpperInvariant().Replace("-", "_").Replace(" ", "_");
        }

        private static string NormalizeIntentText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var normalized = text.ToLowerInvariant()
                .Replace("\u20b9", " rs ")
                .Replace("\u091a\u093e\u0939\u093f\u090f", " chahiye ")
                .Replace("\u091a\u093e\u0939\u093f\u092f\u0947", " chahiye ")
                .Replace("\u091c\u0930\u0942\u0930\u0924", " requirement ")
                .Replace("\u0906\u0935\u0936\u094d\u092f\u0915", " required ")
                .Replace("\u0915\u093f\u0930\u093e\u092f\u093e", " rent ")
                .Replace("\u092c\u0947\u091a\u0928\u093e", " sell ")
                .Replace("\u092c\u093f\u0915\u094d\u0930\u0940", " sale ");
            return Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        public static string SanitizeStringField(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            // Replace common Hindi words/particles in text fields with English equivalents
            var cleaned = text
                .Replace("\u092E\u094E", " ")     // à¤®à¥‡à¤‚ -> space
                .Replace("\u092E\u094E\u0902", " ") // à¤®à¥‡à¤‚ -> space
                .Replace("\u092E\u0947\u0902", " ") // à¤®à¥‡à¤‚ -> space
                .Replace("\u092E\u0947", " ")   // à¤®à¥‡ -> space
                .Replace("\u0915\u0947", " ")   // à¤•à¥‡ -> space
                .Replace("\u092A\u093E\u0938", " near ") // à¤ªà¤¾à¤¸ -> near
                .Replace("\u0938\u093E\u092E\u0928\u0947", " opposite ") // à¤¸à¤¾à¤®à¤¨à¥‡ -> opposite
                .Replace("\u092A\u0940\u091B\u0947", " behind ") // à¤ªà¥€à¤›à¥‡ -> behind
                .Replace("\u092A\u0930", " ") // à¤ªà¤° -> space
                .Replace("\u0938\u0947", " ") // à¤¸à¥‡ -> space
                .Replace("\u0915\u094B", " ") // à¤•à¥‹ -> space
                .Replace("\u0928\u0947", " ") // à¤¨à¥‡ -> space
                .Replace("\u0914\u0930", " and ") // à¤”à¤° -> and
                .Replace("\u0939\u0948", " ") // à¤¹à¥ˆ -> space
                .Replace("\u0939\u0948\u0902", " ") // à¤¹à¥ˆà¤‚ -> space
                .Replace("\u0939\u0947", " ") // à¤¹à¥‡ -> space
                .Replace("\u0915\u093E", " ") // à¤•à¤¾ -> space
                .Replace("\u0915\u0940", " ") // à¤•à¥€ -> space
                .Replace("\u092D\u0940", " ") // à¤­à¥€ -> space
                .Replace("\u0924\u094B", " ") // à¤¤à¥‹ -> space
                .Replace("\u092F\u0938", " ") // à¤‡à¤¸ -> space
                .Replace("\u0909\u0938", " ") // à¤‰à¤¸ -> space
                .Replace("\u0939\u094B", " ") // à¤¹à¥‹ -> space
                .Replace("\u0939\u0940", " ") // à¤¹à¥€ -> space
                
                // Size units
                .Replace("\u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930 \u092B\u0940\u091F", " sqft ")
                .Replace("\u0935\u0930\u094D\u0917 \u092B\u0940\u091F", " sqft ")
                .Replace("\u0935\u0930\u094D\u0917 \u092B\u093C\u0940\u091F", " sqft ")
                .Replace("\u092A\u0930 \u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930", " per sqft ")
                .Replace("\u090F\u0915\u0921\u093C", " acre ")
                .Replace("\u090F\u0915\u0921", " acre ")
                .Replace("\u092C\u0940\u0918\u093E", " bigha ")
                .Replace("\u092C\u093F\u0917\u093E", " bigha ")
                .Replace("\u0915\u0930\u094B\u0921\u093C", " crore ")
                .Replace("\u0915\u0930\u094B\u0921", " crore ")
                .Replace("\u0932\u093E\u0916", " lakh ")

                // Property types
                .Replace("\u092A\u094D\u0932\u0949\u091F", " plot ")
                .Replace("\u092A\u094D\u0932\u093E\u091F", " plot ")
                .Replace("\u092B\u094D\u0932\u0948\u091F", " flat ")
                .Replace("\u092E\u0915\u093E\u0928", " house ")
                .Replace("\u091C\u092E\u0940\u0928", " land ")
                .Replace("\u091C\u093C\u092E\u0940\u0928", " land ")
                .Replace("\u0926\u0941\u0915\u093E\u0928", " shop ")
                .Replace("\u092C\u0902\u0917\u0932\u093E", " bungalow ")
                .Replace("\u0935\u093F\u0932\u093E", " villa ")
                .Replace("\u0917\u094B\u0926\u093E\u092E", " godown ")
                .Replace("\u0936\u094B\u0930\u0942\u092E", " showroom ")
                .Replace("\u092D\u0942\u0916\u0902\u0921", " plot ")

                // Intent keywords
                .Replace("\u0938\u0947\u0932 \u0915\u0930\u0928\u093E \u0939\u0948", " for sale ")
                .Replace("\u0938\u0947\u0932 \u0915\u0930\u0928\u093E", " for sale ")
                .Replace("\u0938\u0947\u0932 \u0939\u0948", " for sale ")
                .Replace("\u092B\u0949\u0930 \u0938\u0947\u0932", " for sale ")
                .Replace("\u0938\u0947\u0932", " sale ")
                .Replace("\u0915\u093F\u0930\u093E\u090F \u0938\u0947 \u0926\u0947\u0928\u093E \u0939\u0948", " available for rent ")
                .Replace("\u0915\u093F\u0930\u093E\u090F \u0938\u0947 \u0926\u0947\u0928\u0940 \u0939\u0948", " available for rent ")
                .Replace("\u0915\u093F\u0930\u093E\u092F\u093E", " rent ")
                .Replace("\u0915\u093F\u0930\u093E\u092F\u0947", " rent ")
                .Replace("\u0915\u093F\u0930\u093E\u090F", " rent ")
                .Replace("\u092C\u0947\u091C\u0928\u093E \u0939\u0948", " for sale ")
                .Replace("\u092C\u0947\u091C\u0928\u093E", " sale ")
                .Replace("\u092C\u093F\u0915\u094D\u0930\u0940", " sale ")
                .Replace("\u092C\u093F\u0915\u093E\u090A", " for sale ")
                .Replace("\u0909\u092A\u0932\u092C\u094D\u0927", " available ")
                .Replace("\u0916\u0930\u0940\u0926\u0928\u093E \u0939\u0948", " requirement ")
                .Replace("\u0916\u0930\u0940\u0926\u0928\u093E", " buy ")
                .Replace("\u0916\u0930\u0940\u0926\u0940", " purchase ")
                .Replace("\u091A\u093E\u0939\u093F\u090F", " chahiye ")
                .Replace("\u0932\u0947\u0928\u093E \u0939\u0948", " required ")

                // Localities
                .Replace("\u0938\u0941\u092A\u0930 \u0915\u0949\u0930\u093F\u0921\u094B\u0930", " super corridor ")
                .Replace("\u0907\u0902\u0926\u094C\u0930", " indore ")
                .Replace("\u0914\u0930\u0940\u0935\u093F\u0902\u0926\u094B", " auravindo ")
                .Replace("\u0914\u0930\u0935\u093F\u0902\u0926\u094B", " auravindo ")
                .Replace("\u0909\u091C\u094D\u091C\u0948\u0928 \u0930\u094B\u0921", " ujjain road ")
                .Replace("\u0916\u0902\u0921\u0935\u093E \u0930\u094B\u0921", " khandwa road ")
                .Replace("\u0928\u0947\u092E\u093E\u0935\u0930 \u0930\u094B\u0921", " neemavar road ")
                .Replace("\u0928\u094D\u092E\u093E\u0935\u0930 \u0930\u094B\u0921", " neemavar road ")
                .Replace("\u0930\u093F\u0902\u0917 \u0930\u094B\u0921", " ring road ")
                .Replace("\u090F\u092C\u0940 \u0930\u094B\u0921", " ab road ")
                .Replace("\u092C\u093E\u092F\u092A\u093E\u0938", " bypass road ")
                .Replace("\u0926\u0947\u0935\u093E\u0938 \u0928\u093E\u0915\u093E", " dewas naka ")
                .Replace("\u0935\u093F\u091C\u092F \u0928\u0917\u0930", " vijay nagar ")
                .Replace("\u092E\u0939\u093E\u0932\u0915\u094D\u0937\u094D\u092E\u0940 \u0928\u0917\u0930", " mahalaxmi nagar ")
                .Replace("\u092E\u0939\u093E\u0932\u0915\u094D\u0937\u094D\u092E\u0940", " mahalaxmi nagar ")
                .Replace("\u092E\u0939\u093E\u0932\u0915\u094D\u0937\u094D\u092E\u093F", " mahalaxmi nagar ")
                .Replace("\u0916\u091C\u0930\u093E\u0928\u093E", " khajrana ")
                .Replace("\u0915\u0928\u093E\u0921\u093C\u093F\u092F\u093E \u0930\u094B\u0921", " kanadia road ")
                .Replace("\u0915\u0928\u093E\u0921\u093C\u093F\u092F\u093E", " kanadia ")
                .Replace("\u0915\u0928\u093E\u0921\u093F\u092F\u093E", " kanadia ")
                .Replace("\u092A\u0932\u093E\u0938\u093F\u092F\u093E", " palasia ")
                .Replace("\u092C\u0902\u0917\u093E\u0932\u094E \u0938\u094D\u0915\u094D\u0935\u093E\u092F\u0930", " bengali square ")
                .Replace("\u0928\u093F\u092A\u093E\u0928\u093F\u092F\u093E", " nipania ")
                .Replace("\u092E\u093E\u0902\u0917\u0932\u093F\u092F\u093E", " mangaliya ")
                .Replace("\u0938\u093E\u0902\u0935\u0947\u0938", " sanwer ")
                .Replace("\u092A\u0930\u094D\u0925\u092E\u092A\u0941\u0930", " pithampur ")
                .Replace("\u092E\u0939\u0942", " mhow ")
                .Replace("\u092C\u093F\u091a\u094b\u0932\u0940", " bicholi hapsi ")
                .Replace("\u092C\u093F\u091a\u094b\u0932\u0940 \u0939\u092a\u0941\u0938\u0940", " bicholi hapsi ")
                .Replace("\u0930\u093E\u091C\u094B\u0926\u093E", " rajoda ")
                .Replace("\u0938\u094D\u0915\u094B\u092E", " scheme ")
                .Replace("\u092D\u093E\u0930 \u0930\u094B\u0921", " Dhar Road ")
                .Replace("\u092C\u0921\u093C\u092C\u0902\u0917\u093E\u0930\u0921\u093E", " Badbangarda ")
                .Replace("\u0938\u0941\u092a\u0930 \u0915\u0949\u0930\u093f\u0921\u094b\u0930", " super corridor ")
                .Replace("\u0907\u0902\u0926\u094c\u0930", " indore ")
                .Replace("\u0914\u0930\u0940\u0935\u093f\u0902\u0926\u094b", " auravindo ")
                .Replace("\u0914\u0930\u0935\u093f\u0902\u0926\u094b", " auravindo ")
                .Replace("\u0909\u091c\u094d\u091c\u0948\u0928 \u0930\u094b\u0921", " ujjain road ")
                .Replace("\u0916\u0902\u0921\u0935\u093e \u0930\u094b\u0921", " khandwa road ")
                .Replace("\u0928\u0947\u092e\u093e\u0935\u0930 \u0930\u094b\u0921", " neemavar road ")
                .Replace("\u0928\u094d\u092e\u093e\u0935\u0930 \u0930\u094b\u0921", " neemavar road ")
                .Replace("\u0930\u093f\u0902\u0917 \u0930\u094b\u0921", " ring road ")
                .Replace("\u090f\u092c\u0940 \u0930\u094b\u0921", " ab road ")
                .Replace("\u092c\u093e\u092f\u092a\u093e\u0938", " bypass road ")
                .Replace("\u0926\u0947\u0935\u093e\u0938 \u0928\u093e\u0915\u093e", " dewas naka ")
                .Replace("\u0935\u093f\u091c\u092f \u0928\u0917\u0930", " vijay nagar ")
                .Replace("\u092e\u0939\u093e\u0932\u0915\u094d\u0937\u094d\u092e\u0940 \u0928\u0917\u0930", " mahalaxmi nagar ")
                .Replace("\u092e\u0939\u093e\u0932\u0915\u094d\u0937\u094d\u092e\u0940", " mahalaxmi nagar ")
                .Replace("\u092e\u0939\u093e\u0932\u0915\u094d\u0937\u094d\u092e\u093f", " mahalaxmi nagar ")
                .Replace("\u0916\u091c\u0930\u093e\u0928\u093e", " khajrana ")
                .Replace("\u0915\u0928\u093e\u0921\u093c\u093f\u092f\u093e \u0930\u094b\u0921", " kanadia road ")
                .Replace("\u0915\u0928\u093e\u0921\u093c\u093f\u092f\u093e", " kanadia ")
                .Replace("\u0915\u0928\u093e\u0921\u093f\u092f\u093e", " kanadia ")
                .Replace("\u092a\u0932\u093e\u0938\u093f\u092f\u093e", " palasia ")
                .Replace("\u092c\u0902\u0917\u093e\u0932\u0940 \u0938\u094d\u0915\u094d\u0935\u093e\u092f\u0930", " bengali square ")
                .Replace("\u0928\u093f\u092a\u093e\u0928\u093f\u092f\u093e", " nipania ")
                .Replace("\u092e\u093e\u0902\u0917\u0928\u093f\u092f\u093e", " mangaliya ")
                .Replace("\u0938\u093e\u0902\u0935\u0947\u0938", " sanwer ")
                .Replace("\u092a\u0930\u094d\u0925\u092e\u092a\u0941\u0930", " pithampur ")
                .Replace("\u092e\u0939\u0942", " mhow ")
                .Replace("\u092c\u093f\u091a\u094b\u0932\u0940", " bicholi hapsi ")
                .Replace("\u092c\u093f\u091a\u094b\u0932\u0940 \u0939\u092a\u0941\u0938\u0940", " bicholi hapsi ")
                .Replace("\u0930\u093e\u091c\u094b\u0926\u093e", " rajoda ")
                .Replace("\u0938\u094d\u0915\u094b\u092e", " scheme ")
                .Replace("\u092d\u093e\u0930 \u0930\u094b\u0921", " Dhar Road ")
                .Replace("\u092c\u0921\u093c\u092c\u0902\u0917\u093e\u0930\u0921\u093e", " Badbangarda ")
                .Replace("\u0938\u093f\u0902\u0907\u0902\u0938\u093e", " Sinhasa ")
                .Replace("\u0938\u093f\u0902\u0938\u093e", " Sinhasa ")
                .Replace("\u092a\u0902\u091c\u092f\u093e", " Panchderia ")
                .Replace("\u092d\u0930\u094d\u092e\u092a\u0940\u0930\u0940", " Dharampuri ")
                .Replace("\u092a\u093e\u0932\u093e\u0916\u0947\u0921\u093c\u094d\u0920\u0940", " palakhedi ")
                .Replace("\u092a\u093e\u0932\u093e\u0916\u0947\u0921\u0940", " palakhedi ")
                .Replace("\u092a\u093e\u0932\u093e\u0916\u0921\u093c\u0940", " palakhedi ")
                
                // Other words
                .Replace("\u092a\u094d\u0930\u094b\u091c\u0947\u0915\u094d\u091f", " project ")
                .Replace("\u0938\u093e\u0907\u091c", " size ")
                .Replace("\u0938\u093e\u0907\u091c\u093c", " size ")
                .Replace("\u0921\u093f\u092e\u093e\u0902\u0921", " demand ")
                .Replace("\u0921\u093f\u092e\u093e\u0902\u092f", " demand ")
                .Replace("\u0930\u0947\u091f", " rate ")
                .Replace("\u0930\u0947\u092f\u0924", " rate ")
                .Replace("\u0930\u091c\u093f\u0938\u094d\u091f\u094d\u0930\u0940", " registry ")
                .Replace("\u092e\u0902\u091c\u093c\u093f\u0932", " floor ")
                .Replace("\u092e\u0902\u091c\u093f\u0932", " floor ")
                .Replace("\u092a\u093e\u0930\u094d\u0915\u093f\u0902\u0917", " parking ")
                .Replace("\u092e\u093f\u0932\u0928 \u0939\u093e\u0907\u091f\u094d\u0938", " Milan Heights ")
                .Replace("\u0905\u0917\u094d\u0930\u0935\u093e\u0932 \u092a\u092c\u094d\u0932\u093f\u0915 \u0938\u094d\u0915\u0942\u0932", " Agarwal Public School ")
                .Replace("\u0926\u0942\u0930\u0940", " distance ")
                .Replace("\u0905\u0930\u094D\u091C\u0947\u0902\u091F", " urgent ")
                .Replace("\u0905\u0930\u094D\u091C\u0947\u0928\u094D\u091F", " urgent ")
                .Replace("\u0938\u0902\u092A\u0930\u094D\u0915 \u0915\u0930\u0947\u0902", " contact ")
                .Replace("\u0938\u0902\u092A\u0930\u094D\u0915 \u0915\u0930\u0947", " contact ")
                .Replace("\u0938\u0902\u092A\u0930\u094D\u0915", " contact ")
                .Replace("\u092B\u0947\u0938\u093F\u0902\u0917", " facing ")
                .Replace("\u0908\u0938\u094D\u091F", " east ")
                .Replace("\u0935\u0947\u0938\u094D\u091F", " west ")
                .Replace("\u092C\u0947\u0938\u094D\u091F", " west ")
                .Replace("\u0928\u093E\u0930\u094D\u092F\u0924", " north ")
                .Replace("\u0938\u093E\u0909\u0925", " south ")
                .Replace("\u0915\u0949\u0930\u094D\u0928\u0930", " corner ")
                .Replace("\u0917\u093E\u0930\u094D\u0921\u0928", " garden ")
                .Replace("\u092B\u094E\u091F", " ft ")
                .Replace("\u092B\u093F\u091F", " ft ")
                .Replace("\u0930\u094B\u0921", " road ");

            // Strip any remaining Devnagari characters (U+0900 to U+097F)
            cleaned = Regex.Replace(cleaned, @"[\u0900-\u097F]+", " ");

            // Collapse multiple spaces
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            return cleaned;
        }

        public static string? SafeFindFile(string dir, string fileName)
        {
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(fileName)) return null;
            try
            {
                var directPath = Path.Combine(dir, fileName);
                if (File.Exists(directPath)) return directPath;

                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(subDir);
                    if (name.StartsWith(".") || 
                        name.Equals("bin", StringComparison.OrdinalIgnoreCase) || 
                        name.Equals("obj", StringComparison.OrdinalIgnoreCase) || 
                        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || 
                        name.Equals("pkg", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var found = SafeFindFile(subDir, fileName);
                    if (found != null) return found;
                }
            }
            catch
            {
                // Suppress unauthorized access exceptions, etc.
            }
            return null;
        }

        public static string? ResolveLocalFilePath(string fileName, ILambdaLogger logger)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            // 1. Check current directory & parent directories
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null)
            {
                var possible = Path.Combine(currentDir.FullName, fileName);
                if (File.Exists(possible)) return possible;
                currentDir = currentDir.Parent;
            }

            // 2. Check current working directory
            var workDir = Directory.GetCurrentDirectory();
            var possibleWork = Path.Combine(workDir, fileName);
            if (File.Exists(possibleWork)) return possibleWork;

            // 3. Search via command line args
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    string? dir = null;
                    if ((arg == "--project-dir" || arg == "-p") && i + 1 < args.Length)
                    {
                        dir = args[i + 1];
                    }
                    else if (Directory.Exists(arg))
                    {
                        dir = arg;
                    }
                    else if (File.Exists(arg))
                    {
                        dir = Path.GetDirectoryName(arg);
                    }

                    if (dir != null && Directory.Exists(dir))
                    {
                        var found = SafeFindFile(dir, fileName);
                        if (found != null) return found;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Command line project dir search failed: {ex.Message}");
            }

            // 4. Try known workspace path on any logical drive
            try
            {
                foreach (var drive in Directory.GetLogicalDrives())
                {
                    var possibleWorkspace = Path.Combine(drive, "Internship's", "prop-seeker", "litrentalsapi");
                    if (Directory.Exists(possibleWorkspace))
                    {
                        logger.LogInformation($"Searching in discovered logical drive workspace: {possibleWorkspace}");
                        var found = SafeFindFile(possibleWorkspace, fileName);
                        if (found != null) return found;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Logical drives search failed: {ex.Message}");
            }

            // 5. Fallback recursive search under working directory
            try
            {
                var found = SafeFindFile(workDir, fileName);
                if (found != null) return found;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Working directory recursive search failed: {ex.Message}");
            }

            return null;
        }
    }
}


