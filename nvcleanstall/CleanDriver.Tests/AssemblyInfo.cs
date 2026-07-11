using Xunit;

// GAP-S02a: run the suite sequentially. Several tests exercise shared static seams by design —
// Jobs.StallTimeout / MaxRetainedJobs / Clock, PackageValidation size caps (set-and-restore in a
// finally) — and this slice added more (the BUG-05 eviction/expiry tests freeze Jobs.Clock and
// shrink the cap). Under xunit's default cross-class parallelism those globals race: one test's
// frozen clock or shrunk cap can evict or mis-time another's job. Candidate #6 flagged this seam
// ("revisit when test parallelism grows"); serializing the assembly closes it for every such
// test at once. The suite runs a little slower but is race-free.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
