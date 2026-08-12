using System.Linq;
using NFluent;
using NUnit.Framework;
using Registry.Compaction;

namespace Registry.Test;

internal class TestHivePruner
{
    [Test]
    public void ShouldPlanPruneKeepingMostRecentSubkeys()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var keyPath = @"Local Settings\Software\Microsoft\Windows\Shell\Bags";

        var plan = HivePruner.PlanPrune(usrclassDeleted, keyPath, 2);

        Check.That(plan.TotalSubkeyCount).IsEqualTo(5);
        Check.That(plan.RetainCount).IsEqualTo(2);
        Check.That(plan.PruneCount).IsEqualTo(3);

        // The two most-recently-written subkeys ("4" and one of the 2011 entries with the latest timestamp
        // among ties) should be retained; everything else should be marked for pruning.
        Check.That(plan.SubkeysToRetain[0]).EndsWith(@"Shell\Bags\4");
        Check.That(plan.SubkeysToPrune).Not.Contains(plan.SubkeysToRetain[0]);
    }

    [Test]
    public void PlanPruneShouldRetainEverythingWhenCountBelowThreshold()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var keyPath = @"Local Settings\Software\Microsoft\Windows\Shell\Bags";

        var plan = HivePruner.PlanPrune(usrclassDeleted, keyPath, 50);

        Check.That(plan.TotalSubkeyCount).IsEqualTo(5);
        Check.That(plan.RetainCount).IsEqualTo(5);
        Check.That(plan.PruneCount).IsEqualTo(0);
    }

    [Test]
    public void PruneKeyToFileShouldDropPrunedSubkeysButKeepRetainedOnesAndRestOfHive()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        var keyPath = @"Local Settings\Software\Microsoft\Windows\Shell\Bags";

        var outPath = @"prunedbags.hve";

        var plan = HivePruner.PruneKeyToFile(usrclassDeleted, keyPath, 2, outPath);

        Check.That(plan.PruneCount).IsEqualTo(3);

        var newReg = new RegistryHive(outPath);
        newReg.ParseHive();

        var prunedKey = newReg.GetKey(keyPath);

        Check.That(prunedKey).IsNotNull();
        Check.That(prunedKey.SubKeys.Count).IsEqualTo(2);
        Check.That(prunedKey.SubKeys.Select(k => k.KeyName)).Contains("4");

        // A sibling subtree unrelated to the pruned key should still be fully intact.
        var muiCacheKey = newReg.GetKey(@"Local Settings\MuiCache\6\52C64B7E");
        Check.That(muiCacheKey).IsNotNull();
        Check.That(muiCacheKey.Values.Count).IsEqualTo(163);
    }

    [Test]
    public void PruneKeyToFileShouldThrowOnUnknownKeyPath()
    {
        var usrclassDeleted = new RegistryHive(@"./Hives/UsrClassDeletedBags.dat");
        usrclassDeleted.RecoverDeleted = true;
        usrclassDeleted.FlushRecordListsAfterParse = false;
        usrclassDeleted.ParseHive();

        Check.ThatCode(() =>
            {
                HivePruner.PlanPrune(usrclassDeleted, @"path\does\not\exist", 5);
            })
            .Throws<System.ArgumentException>();
    }
}
