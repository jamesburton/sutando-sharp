// Several Sutando tests set the SUTANDO_WORKSPACE environment variable to point
// at a per-test temp root. Environment variables are process-global, so running
// these tests in parallel would let them stomp on each other. Disable assembly-
// level parallelization until we've isolated env-var-touching tests behind their
// own collection.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
