# com.arisen.testrunner

The test runner registers a package-only `IApplicationHost`. An ordinary interactive test launch mounts the selected package graph for native-test discovery but does not initialize engine subsystem phases. Native rendering cases therefore own their sole render window, RHI instance, and message loop; close the current case window to advance to the next case.

Kernel smoke mode deliberately bypasses package-only handoff and performs full engine initialization for bounded runtime validation.
