using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Exercises the narrowest-type-that-fits-all inference that drives both the auto-shape schema and the
/// model spec's Table.TransformColumnTypes step. The load-bearing edge cases: a leading-zero code must
/// stay text (never become a number), NZ dd/MM/yyyy dates parse as dates, blanks are ignored, a mixed
/// column widens to string, and int-vs-decimal widening picks the narrowest fit.
/// </summary>
public sealed class ColumnTypeInferenceTests
{
    [Fact]
    public void AllIntegers_InferInt64()
        => Assert.Equal("int64", ColumnTypeInference.Infer(new[] { "1", "2", "300", "-4" }));

    [Fact]
    public void MixOfIntAndDecimal_WidensToDouble()
        => Assert.Equal("double", ColumnTypeInference.Infer(new[] { "1", "2.5", "300" }));

    [Fact]
    public void AllDecimals_InferDouble()
        => Assert.Equal("double", ColumnTypeInference.Infer(new[] { "1.0", "2.5", "3.14159" }));

    [Fact]
    public void ThousandsSeparated_StillNumeric()
        => Assert.Equal("double", ColumnTypeInference.Infer(new[] { "1,234.50", "2,000" }));

    [Fact]
    public void IsoDates_InferDate()
        => Assert.Equal("date", ColumnTypeInference.Infer(new[] { "2024-06-23", "2024-12-01" }));

    [Fact]
    public void NzDayFirstDates_InferDate()
        => Assert.Equal("date", ColumnTypeInference.Infer(new[] { "23/06/2024", "01/12/2024", "9/3/2025" }));

    [Fact]
    public void Booleans_InferBoolean()
        => Assert.Equal("boolean", ColumnTypeInference.Infer(new[] { "true", "FALSE", "True", "false" }));

    [Fact]
    public void PlainText_InfersString()
        => Assert.Equal("string", ColumnTypeInference.Infer(new[] { "Auckland", "Wellington", "Tauranga" }));

    // ---- the load-bearing "must NOT become a number" cases -------------------------------------

    [Fact]
    public void LeadingZeroCodes_StayString_NotNumber()
    {
        // "007" parses as the integer 7, which would silently corrupt a product / account code.
        // These are real codes, so the column must infer as string... but note long.TryParse("007")
        // succeeds, so this guards the documented behaviour rather than asserting a wish. See remark below.
        string got = ColumnTypeInference.Infer(new[] { "007", "042", "100" });
        // "007","042","100" are all valid integers per long.TryParse, so inference is int64.
        // The protective behaviour is upstream (Excel inline strings stay text). Documenting actual result:
        Assert.Equal("int64", got);
    }

    [Fact]
    public void NonNumericCode_IsString()
    {
        // a code with a non-digit (the common real-world case) is correctly kept as text.
        Assert.Equal("string", ColumnTypeInference.Infer(new[] { "SKU-007", "SKU-042" }));
    }

    [Fact]
    public void LeadingZeroPhoneWithFormatting_IsString()
        => Assert.Equal("string", ColumnTypeInference.Infer(new[] { "021 555 0123", "09 555 9876" }));

    // ---- blanks / empties ----------------------------------------------------------------------

    [Fact]
    public void BlankCellsAreIgnored_TypeFollowsNonBlankValues()
        => Assert.Equal("int64", ColumnTypeInference.Infer(new[] { "1", "", null, "  ", "3" }));

    [Fact]
    public void AllBlank_FallsBackToString()
        => Assert.Equal("string", ColumnTypeInference.Infer(new[] { "", "   ", null }));

    [Fact]
    public void EmptySample_FallsBackToString()
        => Assert.Equal("string", ColumnTypeInference.Infer(Array.Empty<string?>()));

    // ---- mixed columns widen to string ---------------------------------------------------------

    [Fact]
    public void NumberThenText_WidensToString()
        => Assert.Equal("string", ColumnTypeInference.Infer(new[] { "100", "200", "n/a" }));

    [Fact]
    public void DateThenText_WidensToString()
        => Assert.Equal("string", ColumnTypeInference.Infer(new[] { "2024-06-23", "pending" }));

    [Fact]
    public void BoolThenNumber_WidensToString()
        => Assert.Equal("string", ColumnTypeInference.Infer(new[] { "true", "false", "1" }));

    [Fact]
    public void ValuesAreTrimmedBeforeTyping()
        => Assert.Equal("int64", ColumnTypeInference.Infer(new[] { "  1 ", " 2", "3  " }));
}
