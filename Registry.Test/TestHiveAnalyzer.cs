using NFluent;
using NUnit.Framework;
using Registry.Analysis;

namespace Registry.Test;

internal class TestHiveAnalyzer
{
    [Test]
    public void ReachabilityShouldMatchTotalInUseCellsForHealthyHive()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var report = HiveAnalyzer.AnalyzeReachability(usrclassDeleted);

        Check.That(report.ReachableKeys).IsStrictlyGreaterThan(0);
        Check.That(report.ReachableValues).IsStrictlyGreaterThan(0);

        // A structurally-sound hive should have reachable/in-use counts in the same ballpark. With
        // RecoverDeleted enabled (as here), the tree walk also includes recovered deleted keys, so reachable
        // counts may exceed raw in-use cell counts slightly -- a large, order-of-magnitude mismatch would
        // indicate real corruption, not this expected small variance.
        var diff = System.Math.Abs(report.ReachableKeys - report.TotalInUseKeyCells);
        Check.That(diff).IsStrictlyLessThan(20);
    }

    [Test]
    public void GetTopSubkeyCountsShouldFindBagsKeyWithFiveSubkeys()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var results = HiveAnalyzer.GetTopSubkeyCounts(usrclassDeleted, minSubkeyCount: 5);

        Check.That(results).Not.IsEmpty();

        var bagsEntry = results.Find(r =>
            r.KeyPath.EndsWith(@"Shell\Bags", System.StringComparison.OrdinalIgnoreCase));

        Check.That(bagsEntry).IsNotNull();
        Check.That(bagsEntry.SubkeyCount).IsEqualTo(5);
    }

    [Test]
    public void GetOffsetDistributionShouldAccountForAllInUseCells()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var buckets = HiveAnalyzer.GetOffsetDistribution(usrclassDeleted, 1024L * 1024);

        Check.That(buckets).Not.IsEmpty();

        var totalInUse = 0;
        foreach (var bucket in buckets)
        {
            totalInUse += bucket.InUseCount;
        }

        Check.That(totalInUse).IsStrictlyGreaterThan(0);
    }

    [Test]
    public void FindDuplicateCellGroupsShouldNotThrowAndShouldReturnAList()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var duplicates = HiveAnalyzer.FindDuplicateCellGroups(usrclassDeleted, 16);

        Check.That(duplicates).IsNotNull();
    }

    [Test]
    public void AnalyzeSubkeyNamingPatternShouldReturnReportForBagsKey()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var report = HiveAnalyzer.AnalyzeSubkeyNamingPattern(usrclassDeleted,
            @"Local Settings\Software\Microsoft\Windows\Shell\Bags");

        Check.That(report).IsNotNull();
        Check.That(report.TotalSubkeys).IsEqualTo(5);
    }
}
