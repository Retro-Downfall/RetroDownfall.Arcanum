CREATE TRIGGER IF NOT EXISTS guild_cohort_eval_buckets_guard_update BEFORE UPDATE ON guild_cohort_eval_buckets
WHEN NEW.CohortId IS NOT OLD.CohortId OR NEW.Generation IS NOT OLD.Generation OR NEW.BucketId IS NOT OLD.BucketId
 OR NEW.AgreementDigest IS NOT OLD.AgreementDigest OR NEW.RepresentativeContentId IS NOT OLD.RepresentativeContentId
 OR NEW.VoteCount<>OLD.VoteCount+1
BEGIN SELECT RAISE(ABORT,'immutable cohort evaluation bucket'); END;
