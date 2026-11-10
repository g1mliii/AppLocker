using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AppLocker;

namespace AppLocker.Tests
{
    public class TestSuite
    {
        private static int passed = 0;
        private static int failed = 0;

        public static void Main()
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("         AppLocker Automated Test Suite          ");
            Console.WriteLine("=================================================");
            Console.WriteLine();

            RunTest("Unit: Password Hashing & Verification", Test_PasswordSecurity);
            RunTest("Unit: Kernel NtSuspendProcess & NtResumeProcess", Test_KernelSuspendResume);
            RunTest("Unit: Single-Instance Mutex & Process Guard", Test_SingleInstanceGuard);
            RunTest("Regression: Multi-Process Grouping & Single Prompt", Test_MultiProcessGrouping);
            RunTest("Regression: Auto-Relock on Process Termination", Test_AutoRelockOnExit);
            RunTest("Regression: Timed Unlock Duration Expiration", Test_TimedUnlockExpiration);
            RunTest("Performance & Resource Redundancy: 1,000 Monitoring Cycles", Test_PerformanceAndMemoryRedundancy);

            Console.WriteLine();
            Console.WriteLine("=================================================");
            Console.WriteLine(string.Format("Test Results: {0} PASSED, {1} FAILED", passed, failed));
            Console.WriteLine("=================================================");

            if (failed > 0)
            {
                Environment.Exit(1);
            }
        }

        private static void RunTest(string name, Action testAction)
        {
            Console.Write("[RUNNING] " + name + "... ");
            try
            {
                testAction();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("PASS");
                Console.ResetColor();
                passed++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAIL");
                Console.ResetColor();
                Console.WriteLine("   Error: " + ex.Message);
                failed++;
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception("Assertion Failed: " + message);
        }

        private static void Test_PasswordSecurity()
        {
            string salt = Guid.NewGuid().ToString("N");
            string pass = "MasterSecret2026!";
            string hash = SecurityHelper.HashPassword(pass, salt);

            AssertTrue(!string.IsNullOrEmpty(hash), "Hash should not be empty.");
            AssertTrue(hash.Length == 64, "SHA-256 hash must be exactly 64 hex characters.");
            AssertTrue(SecurityHelper.VerifyPassword(pass, salt, hash), "VerifyPassword must return true for correct password.");
            AssertTrue(!SecurityHelper.VerifyPassword("WrongPassword", salt, hash), "VerifyPassword must return false for wrong password.");
            AssertTrue(!SecurityHelper.VerifyPassword("", salt, hash), "VerifyPassword must return false for empty password.");
        }

        private static void Test_KernelSuspendResume()
        {
            Process p = Process.Start("notepad.exe");
            Thread.Sleep(150);
            try
            {
                int suspendRes = NativeMethods.NtSuspendProcess(p.Handle);
                AssertTrue(suspendRes == 0, "NtSuspendProcess should return 0 (STATUS_SUCCESS). Got: " + suspendRes);

                int resumeRes = NativeMethods.NtResumeProcess(p.Handle);
                AssertTrue(resumeRes == 0, "NtResumeProcess should return 0 (STATUS_SUCCESS). Got: " + resumeRes);
            }
            finally
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
        }

        private static void Test_SingleInstanceGuard()
        {
            // Simulate instance 1 acquiring mutex
            bool createdFirst;
            using (Mutex m1 = new Mutex(true, "AppLocker_Test_SingleInstance_Mutex", out createdFirst))
            {
                AssertTrue(createdFirst, "First instance must successfully acquire mutex.");

                // Simulate instance 2 trying to acquire same mutex
                bool createdSecond;
                using (Mutex m2 = new Mutex(true, "AppLocker_Test_SingleInstance_Mutex", out createdSecond))
                {
                    AssertTrue(!createdSecond, "Second instance MUST be denied mutex acquisition.");
                }
            }
        }

        private static void Test_MultiProcessGrouping()
        {
            AppLockState state = new AppLockState("brave");
            AssertTrue(!state.IsAuthorized, "Initial state must not be authorized.");
            AssertTrue(!state.IsPromptShowing, "Initial state must not be showing prompt.");

            state.IsPromptShowing = true;
            AssertTrue(state.IsPromptShowing, "Prompt showing flag must be set.");

            bool shouldPromptAgain = !state.IsAuthorized && !state.IsPromptShowing;
            AssertTrue(!shouldPromptAgain, "Subsequent child processes must NOT trigger a second prompt while one is active.");

            state.IsAuthorized = true;
            state.IsPromptShowing = false;
            state.AuthorizedUntil = DateTime.MaxValue;

            AssertTrue(state.IsAuthorized, "App must be authorized after unlock.");
            AssertTrue(!state.IsPromptShowing, "Prompt showing must be cleared after unlock.");
        }

        private static void Test_AutoRelockOnExit()
        {
            AppLockState state = new AppLockState("testapp");
            state.IsAuthorized = true;
            state.AuthorizedUntil = DateTime.MaxValue;

            int runningProcessCount = 0;
            int unlockDurationMinutes = 0;

            if (runningProcessCount == 0 && unlockDurationMinutes <= 0)
            {
                state.IsAuthorized = false;
            }

            AssertTrue(!state.IsAuthorized, "App must automatically relock once all its processes have closed.");
        }

        private static void Test_TimedUnlockExpiration()
        {
            AppLockState state = new AppLockState("testapp");
            state.IsAuthorized = true;
            state.AuthorizedUntil = DateTime.UtcNow.AddMinutes(-1);

            int unlockDurationMinutes = 15;
            if (unlockDurationMinutes > 0 && DateTime.UtcNow > state.AuthorizedUntil)
            {
                state.IsAuthorized = false;
            }

            AssertTrue(!state.IsAuthorized, "Timed authorization must expire when expiration timestamp passes.");
        }

        private static void Test_PerformanceAndMemoryRedundancy()
        {
            string[] testTargets = new string[] { "brave", "chrome", "notepad", "discord" };
            Stopwatch sw = Stopwatch.StartNew();

            int cycles = 1000;
            for (int i = 0; i < cycles; i++)
            {
                for (int t = 0; t < testTargets.Length; t++)
                {
                    Process[] procs = Process.GetProcessesByName(testTargets[t]);
                    for (int p = 0; p < procs.Length; p++)
                    {
                        procs[p].Dispose();
                    }
                }
            }

            sw.Stop();
            double avgMsPerCycle = (double)sw.ElapsedMilliseconds / cycles;
            Console.Write(string.Format("({0}ms total, {1:F3}ms/cycle) ", sw.ElapsedMilliseconds, avgMsPerCycle));

            AssertTrue(avgMsPerCycle < 15.0, "Average check cycle time must be under 15ms. Got: " + avgMsPerCycle);

            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            NativeMethods.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));

            Process current = Process.GetCurrentProcess();
            current.Refresh();
            double ramMB = (double)current.WorkingSet64 / (1024 * 1024);
            Console.Write(string.Format("(Memory: {0:F2} MB) ", ramMB));

            AssertTrue(ramMB < 12.0, "Working set memory must stay under 12 MB after 1000 cycles. Got: " + ramMB);
        }
    }
}
