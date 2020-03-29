//#define TRACEPTW

using System;
using System.Diagnostics;
using System.Collections;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media;
using System.Threading;
using System.Security;
using System.Security.Permissions;
using MS.Internal;
using MS.Internal.PresentationCore;                        // SecurityHelper
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MS.Win32.Penimc;

using SR=MS.Internal.PresentationCore.SR;
using SRID=MS.Internal.PresentationCore.SRID;

namespace System.Windows.Input
{
    /////////////////////////////////////////////////////////////////////////
    internal sealed class PenThreadWorker
    {
         /// <summary>List of constants for PenImc</summary>
        const int PenEventNone           = 0;
        const int PenEventTimeout       = 1;
        const int PenEventPenInRange    = 707;
        const int PenEventPenOutOfRange = 708;
        const int PenEventPenDown       = 709;
        const int PenEventPenUp         = 710;
        const int PenEventPackets       = 711;
        const int PenEventSystem        = 714;
        
        const int MaxContextPerThread  = 31;  // (64 - 1) / 2 = 31.  Max handle limit for MsgWaitForMultipleMessageEx()
        const int EventsFrequency       = 8;

        /// <SecurityNote>
        /// Critical - Marked critical to prevent inadvertant code from modifying this.
        /// </SecurityNote>
        [SecurityCritical]
        IntPtr []             _handles = new IntPtr[0];

        /// <SecurityNote>
        /// Critical - Marked critical to prevent inadvertant code from modifying this.
        /// </SecurityNote>
        [SecurityCritical]
        WeakReference []      _penContexts = new WeakReference[0];

        /// <SecurityNote>
        /// Critical - Marked critical to prevent inadvertant code from modifying this.
        /// </SecurityNote>
        [SecurityCritical]
        IPimcContext2 []       _pimcContexts = new IPimcContext2[0];

        /// <SecurityNote>
        ///     SecurityCritical - This is got under an elevation and is hence critical.
        /// </SecurityNote>
        private SecurityCriticalData<IntPtr>   _pimcResetHandle;
        private volatile bool                  __disposed;
        private List <WorkerOperation>         _workerOperation = new List<WorkerOperation>();
        private object                         _workerOperationLock = new Object();

        // For caching move events.
        
        /// <SecurityNote>
        ///     Critical to prevent accidental spread to transparent code
        /// </SecurityNote>
        [SecurityCritical]
        private PenContext                      _cachedMovePenContext;
        
        private int                             _cachedMoveStylusPointerId;
        private int                             _cachedMoveStartTimestamp;
        private int []                          _cachedMoveData;


        /////////////////////////////////////////////////////////////////////
        //
        // Here's a bunch of helper classes to manage marshalling the calls
        // over to the worker thread to be executed synchronously.
        //
        /////////////////////////////////////////////////////////////////////
        
        // Base class for all worker operations
        private abstract class WorkerOperation
        {
            AutoResetEvent  _doneEvent;

            internal WorkerOperation()
            {
                _doneEvent = new AutoResetEvent(false);
            }

            /// <summary>
            /// Critical - Calls SecurityCritical code OnDoWork which is differred based on the various derived class.
            ///             Called by PenThreadWorker.ThreadProc().
            /// </summary>
            [SecurityCritical]
            internal void DoWork()
            {
                try
                {
                    OnDoWork();
                }
                finally
                {
                    _doneEvent.Set();
                }

            }

            /// <summary>
            /// Critical - Calls SecurityCritical code OnDoWork which is differred based on the various derived class.
            ///             Called by WorkerOperation.DoWork().
            /// </summary>
            [SecurityCritical]
            protected abstract void OnDoWork();

            internal AutoResetEvent DoneEvent
            {
                get { return _doneEvent;}
            }
        }


        // Class that handles getting the current rect for a tablet device.
        private class WorkerOperationThreadStart : WorkerOperation
        {
            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Used to signal when the thread has started up.
            /// </summary>
            protected override void OnDoWork()
            {
                // We don't need to do anything.  Just have event signal we've executed.
            }
        }


        // Class that handles getting the tablet device info for all tablets on the system.
        private class WorkerOperationGetTabletsInfo : WorkerOperation
        {
            internal TabletDeviceInfo[] TabletDevicesInfo
            {
                get { return _tabletDevicesInfo;}
            }

            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Returns the list of TabletDeviceInfo structs that contain information
            ///     about all of the TabletDevices on the system.
            /// </summary>
            /// <SecurityNote>
            ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
            ///               - returns security critical data _pimcTablet
            /// </SecurityNote>
            [SecurityCritical]
            protected override void OnDoWork()
            {
                try
                {
                    // create new collection of tablets
                    MS.Win32.Penimc.IPimcManager2 pimcManager = MS.Win32.Penimc.UnsafeNativeMethods.PimcManager;
                    uint cTablets;
                    pimcManager.GetTabletCount(out cTablets);

                    TabletDeviceInfo[] tablets = new TabletDeviceInfo[cTablets];

                    for ( uint iTablet = 0; iTablet < cTablets; iTablet++ )
                    {
                        MS.Win32.Penimc.IPimcTablet2 pimcTablet;
                        pimcManager.GetTablet(iTablet, out pimcTablet);

                        tablets[iTablet] = PenThreadWorker.GetTabletInfoHelper(pimcTablet);
                    }

                    // Set result data and signal we are done.
                    _tabletDevicesInfo = tablets;
                }
                catch ( System.Runtime.InteropServices.COMException )
                {
                    // result will not be initialized if we fail due to a COM exception.
                    Debug.WriteLine("WorkerOperationGetTabletsInfo.OnDoWork failed due to a COMException");

                    // return no devices found on error.
                    _tabletDevicesInfo = new TabletDeviceInfo[0];
                }
                catch ( System.ArgumentException )
                {
                    // result will not be initialized if we fail due to an ArgumentException.
                    Debug.WriteLine("WorkerOperationGetTabletsInfo.OnDoWork failed due to an ArgumentException");

                    // return no devices found on error.
                    _tabletDevicesInfo = new TabletDeviceInfo[0];
                }
                catch ( System.UnauthorizedAccessException )
                {
                    // result will not be initialized if we fail due to an UnauthorizedAccessException.
                    Debug.WriteLine("WorkerOperationGetTabletsInfo.OnDoWork failed due to an UnauthorizedAccessException");

                    // return no devices found on error.
                    _tabletDevicesInfo = new TabletDeviceInfo[0];
                }
            }

            TabletDeviceInfo[] _tabletDevicesInfo;
        }

        // Class that handles creating a context for a particular tablet device.        
        private class WorkerOperationCreateContext : WorkerOperation
        {
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            internal WorkerOperationCreateContext(IntPtr hwnd, IPimcTablet2 pimcTablet)
            {
                _hwnd = hwnd;
                _pimcTablet = pimcTablet;
            }

            internal PenContextInfo Result
            {
                get { return _result;}
            }

            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Creates a new context for this a window and given tablet device and
            ///     returns a new PenContext in the workOperation class.
            /// </summary>
            /// <SecurityNote>
            ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
            ///               - handle security critical data _hwnd, _pimcTablet.
            /// </SecurityNote>
            [SecurityCritical]
            protected override void OnDoWork()
            {
                IPimcContext2 pimcContext;
                int id;
                Int64 commHandle;

                try
                {
                    _pimcTablet.CreateContext(_hwnd, true, 250, out pimcContext, out id, out commHandle);
                    // Set result data and signal we are done.
                    PenContextInfo result;
                    result.ContextId = id;
                    result.PimcContext = new SecurityCriticalDataClass<IPimcContext2>(pimcContext);

                    // commHandle cannot be a IntPtr by itself because its native counterpart cannot be a
                    // INT_PTR. The reason being that INT_PTR (__int3264) always gets marshalled as a
                    // 32 bit value, which means in a 64 bit process we would lose the first half of the pointer.
                    // Instead with this we always get a 64 bit value and then instantiate the IntPtr appropriately
                    // so that nothing gets lost during marshalling. The cast from Int64 to Int32 below
                    // should be lossless cast because both COM server and client are expected
                    // to be of same bitness (they are in the same process).
                    result.CommHandle = new SecurityCriticalDataClass<IntPtr>((IntPtr.Size == 4 ? new IntPtr((int)commHandle) : new IntPtr(commHandle)));
                    
                    _result = result;
                }
                catch ( System.Runtime.InteropServices.COMException )
                {
                    // result will not be initialized if we fail due to a COM exception.
                    Debug.WriteLine("WorkerOperationCreateContext.OnDoWork failed due to a COMException");

                    // set with uninitialized PenContextInfo (all zero).
                    _result = new PenContextInfo();
                }
                catch ( System.ArgumentException )
                {
                    // result will not be initialized if we fail due to an ArgumentException.
                    Debug.WriteLine("WorkerOperationCreateContext.OnDoWork failed due to an ArgumentException");

                    // set with uninitialized PenContextInfo (all zero).
                    _result = new PenContextInfo();
                }
                catch ( System.UnauthorizedAccessException )
                {
                    // result will not be initialized if we fail due to an UnauthorizedAccessException.
                    Debug.WriteLine("WorkerOperationCreateContext.OnDoWork failed due to an UnauthorizedAccessException");

                    // set with uninitialized PenContextInfo (all zero).
                    _result = new PenContextInfo();
                }
            }

            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            IntPtr       _hwnd;
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            IPimcTablet2  _pimcTablet;
            PenContextInfo _result;
        }

        // Class that handles refreshing the cursor devices for a particular tablet device.        
        private class WorkerOperationRefreshCursorInfo : WorkerOperation
        {
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            internal WorkerOperationRefreshCursorInfo(IPimcTablet2 pimcTablet)
            {
                _pimcTablet = pimcTablet;
            }

            internal StylusDeviceInfo[] StylusDevicesInfo
            {
                get
                {
                    return _stylusDevicesInfo;
                }
            }

            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Causes the stylus devices info (cursors) in penimc to be refreshed 
            ///     for the passed in IPimcTablet2. 
            /// </summary>
            /// <SecurityNote>
            ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
            ///               - handle security critical data _pimcTablet.
            /// </SecurityNote>
            [SecurityCritical]
            protected override void OnDoWork()
            {
                try
                {
                    _pimcTablet.RefreshCursorInfo();
                    _stylusDevicesInfo = PenThreadWorker.GetStylusDevicesInfo(_pimcTablet);
                }
                catch ( System.Runtime.InteropServices.COMException )
                {
                    // result will not be initialized if we fail due to a COM exception.
                    Debug.WriteLine("WorkerOperationRefreshCursorInfo.OnDoWork failed due to a COMException");
                }
                catch ( System.ArgumentException )
                {
                    // result will not be initialized if we fail due to a ArgumentException.
                    Debug.WriteLine("WorkerOperationRefreshCursorInfo.OnDoWork failed due to an ArgumentException");
                }
                catch ( System.UnauthorizedAccessException )
                {
                    // result will not be initialized if we fail due to an UnauthorizedAccessException.
                    Debug.WriteLine("WorkerOperationRefreshCursorInfo.OnDoWork failed due to an UnauthorizedAccessException");
                }
            }

            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            IPimcTablet2 _pimcTablet;

            StylusDeviceInfo[]  _stylusDevicesInfo;
        }

        // Class that handles getting info about a specific tablet device.
        private class WorkerOperationGetTabletInfo : WorkerOperation
        {
            internal WorkerOperationGetTabletInfo(uint index)
            {
                _index = index;
            }

            internal TabletDeviceInfo TabletDeviceInfo
            {
                get { return _tabletDeviceInfo;}
            }

            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Fills in a struct containing the list of TabletDevice properties for
            ///     a given tablet device index.
            /// </summary>
            /// <SecurityNote>
            ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
            ///               - returns security critical data _pimcTablet
            /// </SecurityNote>
            [SecurityCritical]
            protected override void OnDoWork()
            {
                try
                {
                    // create new collection of tablets
                    MS.Win32.Penimc.IPimcManager2 pimcManager = MS.Win32.Penimc.UnsafeNativeMethods.PimcManager;
                    MS.Win32.Penimc.IPimcTablet2 pimcTablet;
                    pimcManager.GetTablet(_index, out pimcTablet);

                    // Set result data and signal we are done.
                    _tabletDeviceInfo = PenThreadWorker.GetTabletInfoHelper(pimcTablet);
                }
                catch ( System.Runtime.InteropServices.COMException )
                {
                    // result will not be initialized if we fail due to a COM exception.
                    Debug.WriteLine("WorkerOperationGetTabletInfo.OnDoWork failed due to COMException");

                    // set to uninitialized TabletDeviceInfo struct (all zeros) to signal failure.
                    _tabletDeviceInfo = new TabletDeviceInfo();
                }
                catch ( System.ArgumentException )
                {
                    // result will not be initialized if we fail due to an ArgumentException.
                    Debug.WriteLine("WorkerOperationGetTabletInfo.OnDoWork failed due to an ArgumentException");

                    // set to uninitialized TabletDeviceInfo struct (all zeros) to signal failure.
                    _tabletDeviceInfo = new TabletDeviceInfo();
                }
                catch ( System.UnauthorizedAccessException )
                {
                    // result will not be initialized if we fail due to an UnauthorizedAccessException.
                    Debug.WriteLine("WorkerOperationGetTabletInfo.OnDoWork failed due to an UnauthorizedAccessException");

                    // set to uninitialized TabletDeviceInfo struct (all zeros) to signal failure.
                    _tabletDeviceInfo = new TabletDeviceInfo();
                }
            }

            uint             _index;
            TabletDeviceInfo _tabletDeviceInfo;
        }
        
        // Class that handles getting the current rect for a tablet device.
        private class WorkerOperationWorkerGetUpdatedSizes : WorkerOperation
        {
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            internal WorkerOperationWorkerGetUpdatedSizes(IPimcTablet2 pimcTablet)
            {
                _pimcTablet = pimcTablet;
            }

            internal TabletDeviceSizeInfo TabletDeviceSizeInfo
            {
                get { return _tabletDeviceSizeInfo;}
            }


            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Gets the current rectangle for a tablet device and returns in workOperation class.
            /// </summary>
            /// <SecurityNote>
            ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
            ///               - handles security critical data _pimcTablet
            /// </SecurityNote>
            [SecurityCritical]
            protected override void OnDoWork()
            {
                try
                {
                    int displayWidth, displayHeight, tabletWidth, tabletHeight;
                    _pimcTablet.GetTabletAndDisplaySize(out tabletWidth, out tabletHeight, out displayWidth, out displayHeight);

                    // Set result data and signal we are done.
                    _tabletDeviceSizeInfo = new TabletDeviceSizeInfo(
                                        new Size( tabletWidth, tabletHeight), 
                                        new Size( displayWidth, displayHeight));
                }
                catch ( System.Runtime.InteropServices.COMException )
                {
                    // result will not be initialized if we fail due to a COM exception.
                    Debug.WriteLine("WorkerOperationWorkerGetUpdatedSizes.OnDoWork failed due to a COMException");

                    // Size structs will be 1x1 if a COM exception is thrown.  Should be dead context anyway on exception.
                    _tabletDeviceSizeInfo = new TabletDeviceSizeInfo(new Size( 1, 1), new Size( 1, 1));
                }
                catch ( System.UnauthorizedAccessException )
                {
                    // result will not be initialized if we fail due to an UnauthorizedAccessException.
                    Debug.WriteLine("WorkerOperationWorkerGetUpdatedSizes.OnDoWork failed due to an UnauthorizedAccessException");

                    // Size structs will be 1x1 if a COM exception is thrown.  Should be dead context anyway on exception.
                    _tabletDeviceSizeInfo = new TabletDeviceSizeInfo(new Size( 1, 1), new Size( 1, 1));
                }
            }

            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            IPimcTablet2          _pimcTablet;
            TabletDeviceSizeInfo _tabletDeviceSizeInfo;
        }


        // Class that handles getting the current rect for a tablet device.
        private class WorkerOperationAddContext : WorkerOperation
        {
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            internal WorkerOperationAddContext(PenContext penContext, PenThreadWorker penThreadWorker)
            {
                _newPenContext = penContext;
                _penThreadWorker = penThreadWorker;
            }

            internal bool Result
            {
                get { return _result;}
            }

            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Adds a PenContext to the list of contexts that events can be received
            ///     from and returns whether it was successful in workOperation class.
            /// </summary>
            /// <SecurityNote>
            ///     Critical: - handles security critical data _penContexts, _handles, _pimcContexts
            /// </SecurityNote>
            [SecurityCritical]
            protected override void OnDoWork()
            {
                _result = _penThreadWorker.AddPenContext(_newPenContext);
            }
                    
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            PenContext      _newPenContext;
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            PenThreadWorker _penThreadWorker;

            bool _result;
        }


        // Class that handles getting the current rect for a tablet device.
        private class WorkerOperationRemoveContext : WorkerOperation
        {
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            internal WorkerOperationRemoveContext(PenContext penContext, PenThreadWorker penThreadWorker)
            {
                _penContextToRemove = penContext;
                _penThreadWorker = penThreadWorker;
            }

            internal bool Result
            {
                get { return _result;}
            }

            /////////////////////////////////////////////////////////////////////////
            /// <summary>
            ///     Adds a PenContext to the list of contexts that events can be received
            ///     from and returns whether it was successful in workOperation class.
            /// </summary>
            /// <SecurityNote>
            ///     Critical: - handles security critical data _penContexts, _handles, _pimcContexts
            /// </SecurityNote>
            [SecurityCritical]
            protected override void OnDoWork()
            {
                _result = _penThreadWorker.RemovePenContext(_penContextToRemove);
            }
                    
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            PenContext  _penContextToRemove;
            /// <SecurityNote>
            ///     Critical - Critical data got under an elevation and is hence critical.
            /// </SecurityNote>
            [SecurityCritical]
            PenThreadWorker _penThreadWorker;

            bool _result;
        }


        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        ///    Critical - Calls SecurityCritical code MS.Win32.Penimc.UnsafeNativeMethods.CreateResetEvent
        ///         and handles SecurityCritical data resetHandle.
        ///             Called by PenThread constructor.
        ///             TreatAsSafe boundry is Stylus.EnableCore, Stylus.RegisterHwndForInput
        ///                and HwndWrapperHook class (via HwndSource.InputFilterMessage).
        /// </SecurityNote>
        [SecurityCritical]
        internal PenThreadWorker()
        {
            IntPtr resetHandle;
            // Consider: We could use a AutoResetEvent handle instead and avoid the penimc.dll call.
            MS.Win32.Penimc.UnsafeNativeMethods.CreateResetEvent(out resetHandle);
            _pimcResetHandle = new SecurityCriticalData<IntPtr>(resetHandle);

            WorkerOperationThreadStart started = new WorkerOperationThreadStart();
            lock(_workerOperationLock)
            {
                _workerOperation.Add((WorkerOperation)started);
            }

            Thread thread = new Thread(new ThreadStart(ThreadProc));
            thread.IsBackground = true; // don't hold process open due to this thread.
            thread.Start();
            
            // Wait for this work to be completed (ie thread is started up).
            started.DoneEvent.WaitOne();
            started.DoneEvent.Close();
        }

        /// <SecurityNote>
        /// Critical - Needs to call SupressUnmanagedCodeSecurity attributed
        ///               code to free unmanaged resource handle.  Needs to be
        ///               SecurityCritical for that.  Also references SecurityCriticalData.
        /// </SecurityNote>
        [SecurityCritical]
        internal void Dispose()
        {
            if(!__disposed)
            {
                __disposed = true;
                
                // Kick thread to wake up and see we are disposed.
                MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);
                // Let it destroy the reset event.
            }
            GC.KeepAlive(this);
        }

        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent),
        ///             accesses SecurityCriticalData _pimcResetHandle and handles SecurityCritical data penContext.
        ///             Called by PenThread.AddPenContext.
        /// </SecurityNote>
        [SecurityCritical]
        internal bool WorkerAddPenContext(PenContext penContext)
        {
            if (__disposed)
            {
                throw new ObjectDisposedException(null, SR.Get(SRID.Penservice_Disposed));
            }

            Debug.Assert(penContext != null);
            
            WorkerOperationAddContext addContextOperation = new WorkerOperationAddContext(penContext, this);

            lock(_workerOperationLock)
            {
                _workerOperation.Add(addContextOperation);
            }

            // Kick thread to do this work.
            MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);

            // Wait for this work to be completed.
            addContextOperation.DoneEvent.WaitOne();
            addContextOperation.DoneEvent.Close();

            return addContextOperation.Result;
        }


        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent),
        ///             accesses SecurityCriticalData _pimcResetHandle and handles SecurityCritical data penContext.
        ///             Called by PenThread.Disable.
        /// </SecurityNote>
        [SecurityCritical]
        internal bool WorkerRemovePenContext(PenContext penContext)
        {
            if (__disposed)
            {
                return true;
            }

            Debug.Assert(penContext != null);
            
            WorkerOperationRemoveContext removeContextOperation = new WorkerOperationRemoveContext(penContext, this);

            lock(_workerOperationLock)
            {
                _workerOperation.Add(removeContextOperation);
            }

            // Kick thread to do this work.
            MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);

            // Wait for this work to be completed.
            removeContextOperation.DoneEvent.WaitOne();
            removeContextOperation.DoneEvent.Close();

            return removeContextOperation.Result;
        }

        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent),
        ///             accesses SecurityCriticalData _pimcResetHandle.
        ///             Called by PenThreadPool.WorkerGetTabletsInfo.
        /// </SecurityNote>
        [SecurityCritical]
        internal TabletDeviceInfo[] WorkerGetTabletsInfo()
        {
            // Set data up for this call
            WorkerOperationGetTabletsInfo getTablets = new WorkerOperationGetTabletsInfo();
            
            lock(_workerOperationLock)
            {
                _workerOperation.Add(getTablets);
            }

            // Kick thread to do this work.
            MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);

            // Wait for this work to be completed.
            getTablets.DoneEvent.WaitOne();
            getTablets.DoneEvent.Close();
        
            return getTablets.TabletDevicesInfo;
        }


        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent),
        ///             accesses SecurityCriticalData _pimcResetHandle and handles SecurityCritical data
        ///             (hwnd, pimcTablet).
        ///             Called by PenThreadPool.WorkerCreateContext.
        ///             TreatAsSafe boundry is Stylus.EnableCore and HwndWrapperHook class 
        ///             (via HwndSource.InputFilterMessage).
        /// </SecurityNote>
        [SecurityCritical]
        internal PenContextInfo WorkerCreateContext(IntPtr hwnd, IPimcTablet2 pimcTablet)
        {
            WorkerOperationCreateContext createContextOperation = new WorkerOperationCreateContext(
                                                                    hwnd,
                                                                    pimcTablet);
            lock(_workerOperationLock)
            {
                _workerOperation.Add(createContextOperation);
            }

            // Kick thread to do this work.
            MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);

            // Wait for this work to be completed.
            createContextOperation.DoneEvent.WaitOne();
            createContextOperation.DoneEvent.Close();

            return createContextOperation.Result;
        }

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent),
        ///             accesses SecurityCriticalData _pimcResetHandle and handles SecurityCritical data pimcTablet.
        ///             Called by PenThreadPool.WorkerRefreshCursorInfo.
        /// </SecurityNote>
        [SecurityCritical]
        internal StylusDeviceInfo[] WorkerRefreshCursorInfo(IPimcTablet2 pimcTablet)
        {
            WorkerOperationRefreshCursorInfo refreshCursorInfo = new WorkerOperationRefreshCursorInfo(
                                                                 pimcTablet);
            lock(_workerOperationLock)
            {
                _workerOperation.Add(refreshCursorInfo);
            }

            // Kick thread to do this work.
            MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);

            // Wait for this work to be completed.
            refreshCursorInfo.DoneEvent.WaitOne();
            refreshCursorInfo.DoneEvent.Close();

            return refreshCursorInfo.StylusDevicesInfo;
        }

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent),
        ///             accesses SecurityCriticalData _pimcResetHandle.
        ///             Called by PenThreadPool.WorkerGetTabletInfo.
        /// </SecurityNote>
        [SecurityCritical]
        internal TabletDeviceInfo WorkerGetTabletInfo(uint index)
        {
            // Set up data for call
            WorkerOperationGetTabletInfo getTabletInfo = new WorkerOperationGetTabletInfo(
                                                             index);
            lock(_workerOperationLock)
            {
                _workerOperation.Add(getTabletInfo);
            }

            // Kick thread to do this work.
            MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);

            // Wait for this work to be completed.
            getTabletInfo.DoneEvent.WaitOne();
            getTabletInfo.DoneEvent.Close();

            return getTabletInfo.TabletDeviceInfo;
        }

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent),
        ///             accesses SecurityCriticalData _pimcResetHandle and pimcTablet.
        ///             Called by PenThreadPool.WorkerGetUpdatedTabletRect.
        /// </SecurityNote>
        [SecurityCritical]
        internal TabletDeviceSizeInfo WorkerGetUpdatedSizes(IPimcTablet2 pimcTablet)
        {           
            // Set data up for call
            WorkerOperationWorkerGetUpdatedSizes getUpdatedSizes = new WorkerOperationWorkerGetUpdatedSizes(pimcTablet);
            lock(_workerOperationLock)
            {
                _workerOperation.Add(getUpdatedSizes);
            }

            // Kick thread to do this work.
            MS.Win32.Penimc.UnsafeNativeMethods.RaiseResetEvent(_pimcResetHandle.Value);

            // Wait for this work to be completed.
            getUpdatedSizes.DoneEvent.WaitOne();
            getUpdatedSizes.DoneEvent.Close();
            
            return getUpdatedSizes.TabletDeviceSizeInfo;
        }

        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code PenContext.FirePenInRange and PenContext.FirePackets.
        ///             Called by FireEvent and ThreadProc.
        ///             TreatAsSafe boundry is ThreadProc.
        /// </SecurityNote>
        [SecurityCritical]
        void FlushCache(bool goingOutOfRange)
        {
            // Force any cached move/inairmove data to be flushed if we have any.
            if (_cachedMoveData != null)
            {
                // If we are going out of range and this stylus id is not currently in range
                // then eat these cached events (keeps from going in and out of range quickly)
                if (!goingOutOfRange || _cachedMovePenContext.IsInRange(_cachedMoveStylusPointerId))
                {
                    _cachedMovePenContext.FirePenInRange(_cachedMoveStylusPointerId, _cachedMoveData, _cachedMoveStartTimestamp);
                    _cachedMovePenContext.FirePackets(_cachedMoveStylusPointerId, _cachedMoveData, _cachedMoveStartTimestamp);
                }

                _cachedMoveData = null;
                _cachedMovePenContext = null;
                _cachedMoveStylusPointerId = 0;
            }
        }

        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        /// SecurityCritical: Accesses SecurityCritical data _cachedMovePenContext.
        /// </SecurityNote>
        [SecurityCritical]
        bool DoCacheEvent(int evt, PenContext penContext, int stylusPointerId, int [] data, int timestamp)
        {
            // NOTE: Big assumption is that we always get other events between packets (ie don't get move
            // down position followed by move in up position).  We don't account for that here but it should
            // never happen.
            if (evt == PenEventPackets)
            {
                // If no cache then just cache it.
                if (_cachedMoveData == null)
                {
                    _cachedMovePenContext = penContext;
                    _cachedMoveStylusPointerId = stylusPointerId;
                    _cachedMoveStartTimestamp = timestamp;
                    _cachedMoveData = data;
                    return true;
                }
                else if (_cachedMovePenContext == penContext && stylusPointerId == _cachedMoveStylusPointerId)
                {
                    int sinceBeginning = timestamp - _cachedMoveStartTimestamp;
                    if (timestamp < _cachedMoveStartTimestamp)
                        sinceBeginning = (Int32.MaxValue - _cachedMoveStartTimestamp) + timestamp;

                    if (EventsFrequency > sinceBeginning)
                    {
                        // Add to cache data
                        int[] data0 = _cachedMoveData;
                        _cachedMoveData = new int [data0.Length + data.Length];
                        data0.CopyTo(_cachedMoveData, 0);
                        data.CopyTo(_cachedMoveData, data0.Length);
                        return true;
                    }
                }
            }

            return false;
        }

        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code FlushCache, PenContext.FirePenDown, PenContext.FirePenUp, 
        ///             PenContext.FirePenInRange, PenContext.FirePackets, PenContext.FirePenOutOfRange, PenContext.FireSystemGesture.
        ///             Called by ThreadProc.
        ///             TreatAsSafe boundry is ThreadProc.
        /// </SecurityNote>
        [SecurityCritical]
        internal void FireEvent(PenContext penContext, int evt, int stylusPointerId, int cPackets, int cbPacket, IntPtr pPackets)
        {
            // disposed?
            if (__disposed)
            {
                return;  // Don't process this event if we're in the process of shutting down.
            }

            // marshal the data to our cache
            if (cbPacket % 4 != 0)
            {
                throw new InvalidOperationException(SR.Get(SRID.PenService_InvalidPacketData));
            }

            int cItems = cPackets * (cbPacket / 4);
            int[] data = null;
            if (0 < cItems)
            {
                data = new int [cItems]; // GetDataArray(cItems); // see comment on GetDataArray
                Marshal.Copy(pPackets, data, 0, cItems);
                penContext.CheckForRectMappingChanged(data, cPackets);
            }
            else
            {
                data = null;
            }

            int timestamp = Environment.TickCount;
            
            // Deal with caching packet data.
            if (DoCacheEvent(evt, penContext, stylusPointerId, data, timestamp))
            {
                return;
            }
            else
            {
                FlushCache(false);  // make sure we flush cache if not caching.
            }

            //
            // fire it
            //
            switch (evt)
            {
                case PenEventPenDown:
                    penContext.FirePenInRange(stylusPointerId, data, timestamp);
                    penContext.FirePenDown(stylusPointerId, data, timestamp);
                    break;

                case PenEventPenUp:
                    penContext.FirePenInRange(stylusPointerId, data, timestamp);
                    penContext.FirePenUp(stylusPointerId, data, timestamp);
                    break;

                case PenEventPackets:
                    penContext.FirePenInRange(stylusPointerId, data, timestamp);
                    penContext.FirePackets(stylusPointerId, data, timestamp);
                    break;

                case PenEventPenInRange:
                    // We fire this special event just to give the app thread an early peak at
                    // the inrange to filter out mouse moves before we get our first stylus event.
                    penContext.FirePenInRange(stylusPointerId, null, timestamp);
                    break;

                case PenEventPenOutOfRange:
                    penContext.FirePenOutOfRange(stylusPointerId, timestamp);
                    break;

                case PenEventSystem:
                    penContext.FireSystemGesture(stylusPointerId, timestamp);
                    break;
            }
        }

        /////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns a struct containing the list of TabletDevice properties for
        ///     a given tablet device (pimcTablet).
        /// </summary>
        /// <SecurityNote>
        ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
        ///               - handles security critical data pimcTablet
        /// </SecurityNote>
        [SecurityCritical]
        private static TabletDeviceInfo GetTabletInfoHelper(IPimcTablet2 pimcTablet)
        {
            TabletDeviceInfo tabletInfo = new TabletDeviceInfo();

            tabletInfo.PimcTablet = new SecurityCriticalDataClass<IPimcTablet2>(pimcTablet);
            pimcTablet.GetKey(out tabletInfo.Id);
            pimcTablet.GetName(out tabletInfo.Name);
            pimcTablet.GetPlugAndPlayId(out tabletInfo.PlugAndPlayId);
            int iTabletWidth, iTabletHeight, iDisplayWidth, iDisplayHeight;
            pimcTablet.GetTabletAndDisplaySize(out iTabletWidth, out iTabletHeight, out iDisplayWidth, out iDisplayHeight);
            tabletInfo.SizeInfo = new TabletDeviceSizeInfo(new Size(iTabletWidth, iTabletHeight),
                                                           new Size(iDisplayWidth, iDisplayHeight));
            int caps;
            pimcTablet.GetHardwareCaps(out caps);
            tabletInfo.HardwareCapabilities = (TabletHardwareCapabilities)caps;
            int deviceType;
            pimcTablet.GetDeviceType(out deviceType);
            tabletInfo.DeviceType = (TabletDeviceType)(deviceType -1);

            // NTRAID:WINDOWSOS#1679154-2006/06/09-WAYNEZEN,
            // REENTRANCY NOTE: Let a PenThread do this work to avoid reentrancy!
            //                  The IPimcTablet2 object is created in the pen thread. If we access it from the UI thread,
            //                  COM will set up message pumping which will cause reentrancy here.
            InitializeSupportedStylusPointProperties(pimcTablet, tabletInfo);
            tabletInfo.StylusDevicesInfo = GetStylusDevicesInfo(pimcTablet);
            
            return tabletInfo;
        }

        /////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Initializing the supported stylus point properties. and returns in workOperation class.
        /// </summary>
        /// <SecurityNote>
        ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
        ///               - handles security critical data pimcTablet
        /// </SecurityNote>
        [SecurityCritical]
        private static void InitializeSupportedStylusPointProperties(IPimcTablet2 pimcTablet, TabletDeviceInfo tabletInfo)
        {
            int cProps;
            int cButtons;
            int pressureIndex = -1;

            pimcTablet.GetPacketDescriptionInfo(out cProps, out cButtons); // Calls Unmanaged code - SecurityCritical with SUC.
            List<StylusPointProperty> properties = new List<StylusPointProperty>(cProps + cButtons + 3);
            for ( int i = 0; i < cProps; i++ )
            {
                Guid guid;
                int min, max;
                int units;
                float res;
                pimcTablet.GetPacketPropertyInfo(i, out guid, out min, out max, out units, out res); // Calls Unmanaged code - SecurityCritical with SUC.

                if ( pressureIndex == -1 && guid == StylusPointPropertyIds.NormalPressure )
                {
                    pressureIndex = i;
                }

                StylusPointProperty property = new StylusPointProperty(guid, false);
                properties.Add(property);
            }

            for ( int i = 0; i < cButtons; i++ )
            {
                Guid buttonGuid;
                pimcTablet.GetPacketButtonInfo(i, out buttonGuid); // Calls Unmanaged code - SecurityCritical with SUC.

                StylusPointProperty buttonProperty = new StylusPointProperty(buttonGuid, true);
                properties.Add(buttonProperty);
            }

            //validate we can never get X, Y at index != 0, 1
            Debug.Assert(properties[StylusPointDescription.RequiredXIndex /*0*/].Id == StylusPointPropertyIds.X, "X isn't where we expect it! Fix PenImc to ask for X at index 0");
            Debug.Assert(properties[StylusPointDescription.RequiredYIndex /*1*/].Id == StylusPointPropertyIds.Y, "Y isn't where we expect it! Fix PenImc to ask for Y at index 1");
            // NOTE: We can't force pressure since touch digitizers may not provide this info.  The following assert is bogus.
            //Debug.Assert(pressureIndex == -1 || pressureIndex == StylusPointDescription.RequiredPressureIndex /*2*/,
            //    "Fix PenImc to ask for NormalPressure at index 2!");

            if ( pressureIndex == -1 )
            {
                //pressure wasn't found.  Add it
                properties.Insert(StylusPointDescription.RequiredPressureIndex /*2*/, System.Windows.Input.StylusPointProperties.NormalPressure);
            }
            else
            {
                //this device supports pressure
                tabletInfo.HardwareCapabilities |= TabletHardwareCapabilities.SupportsPressure;
            }

            tabletInfo.StylusPointProperties = new ReadOnlyCollection<StylusPointProperty>(properties);
            tabletInfo.PressureIndex = pressureIndex;
        }

        /////////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Getting the cursor info of the stylus devices.
        /// </summary>
        /// <SecurityNote>
        ///     Critical: - calls into unmanaged code that is SecurityCritical with SUC attribute.
        ///               - handles security critical data pimcTablet
        /// </SecurityNote>
        [SecurityCritical]
        private static StylusDeviceInfo[] GetStylusDevicesInfo(IPimcTablet2 pimcTablet)
        {
            int cCursors;

            pimcTablet.GetCursorCount(out cCursors); // Calls Unmanaged code - SecurityCritical with SUC.

            StylusDeviceInfo[] stylusDevicesInfo = new StylusDeviceInfo[cCursors];

            for ( int iCursor = 0; iCursor < cCursors; iCursor++ )
            {
                string sCursorName;
                int cursorId;
                bool fCursorInverted;
                pimcTablet.GetCursorInfo(iCursor, out sCursorName, out cursorId, out fCursorInverted); // Calls Unmanaged code - SecurityCritical with SUC.

                int cButtons;

                pimcTablet.GetCursorButtonCount(iCursor, out cButtons); // Calls Unmanaged code - SecurityCritical with SUC.
                StylusButton[] buttons = new StylusButton[cButtons];
                for ( int iButton = 0; iButton < cButtons; iButton++ )
                {
                    string sButtonName;
                    Guid buttonGuid;
                    pimcTablet.GetCursorButtonInfo(iCursor, iButton, out sButtonName, out buttonGuid); // Calls Unmanaged code - SecurityCritical with SUC.
                    buttons[iButton] = new StylusButton(sButtonName, buttonGuid);
                }
                StylusButtonCollection buttonCollection = new StylusButtonCollection(buttons);

                stylusDevicesInfo[iCursor].CursorName = sCursorName;
                stylusDevicesInfo[iCursor].CursorId = cursorId;
                stylusDevicesInfo[iCursor].CursorInverted = fCursorInverted;
                stylusDevicesInfo[iCursor].ButtonCollection = buttonCollection;
            }

            return stylusDevicesInfo;
        }


        /// <SecurityNote>
        /// Critical - Accesses SecurityCriticalData (penContext, _penContexts, PenContext.CommHandle,
        ///             _pimcContexts, and _handles).
        /// </SecurityNote>
        [SecurityCritical]
        internal bool AddPenContext(PenContext penContext)
        {
            List <PenContext> penContextRefs = new List<PenContext>(); // keep them alive while processing!
            int i;
            bool result = false;

            // Now go through and figure out the good entries
            // Need to clean up the list for gc'd references.
            for (i=0; i<_penContexts.Length; i++)
            {
                if (_penContexts[i].IsAlive)
                {
                    PenContext pc = _penContexts[i].Target as PenContext;
                    // We only need to ref if we have a penContext.
                    if (pc != null)
                    {
                        penContextRefs.Add(pc);
                    }
                }
            }

            // Now try again to see if we have room.
            if (penContextRefs.Count < MaxContextPerThread)
            {
                penContextRefs.Add(penContext); // add the new one to our list.

                // Now build up the handle array and PimcContext ref array.
                _pimcContexts = new IPimcContext2[penContextRefs.Count];
                _penContexts = new WeakReference[penContextRefs.Count];
                _handles = new IntPtr[penContextRefs.Count];
                
                for (i=0; i < penContextRefs.Count; i++)
                {
                    PenContext pc = penContextRefs[i];
                    // We'd have hole in our array if this ever happened.
                    Debug.Assert(pc != null && pc.CommHandle != IntPtr.Zero);
                    _handles[i] = pc.CommHandle; // Add to array.
                    _pimcContexts[i] = pc._pimcContext.Value;
                    _penContexts[i] = new WeakReference(pc);
                    pc = null;
                }

                result = true;
            }

            // Now clean up old refs and assign new array.
            penContextRefs.Clear(); // Make sure we remove refs!
            penContextRefs = null;

            return result;
        }


        /// <SecurityNote>
        /// Critical - Accesses SecurityCriticalData (penContext, _penContexts, PenContext.CommHandle,
        ///             _pimcContexts, and _handles).
        /// </SecurityNote>
        [SecurityCritical]
        internal bool RemovePenContext(PenContext penContext)
        {
            List <PenContext> penContextRefs = new List<PenContext>(); // keep them alive while processing!
            int i;
            bool removed = false;

            // Now go through and figure out the good entries
            // Need to clean up the list for gc'd references.
            for (i=0; i<_penContexts.Length; i++)
            {
                if (_penContexts[i].IsAlive)
                {
                    PenContext pc = _penContexts[i].Target as PenContext;
                    // See if we should keep this PenContext.  
                    // We keep if not GC'd and not the removing one (except if it is 
                    // in range where we need to wait till it goes out of range).
                    if (pc != null && (pc != penContext || pc.IsInRange(0)))
                    {
                        penContextRefs.Add(pc);
                    }
                }
            }

            removed = !penContextRefs.Contains(penContext);

            // Now build up the handle array and PimcContext ref array.
            _pimcContexts = new IPimcContext2[penContextRefs.Count];
            _penContexts = new WeakReference[penContextRefs.Count];
            _handles = new IntPtr[penContextRefs.Count];
            
            for (i=0; i < penContextRefs.Count; i++)
            {
                PenContext pc = penContextRefs[i];
                // We'd have hole in our array if this ever happened.
                Debug.Assert(pc != null && pc.CommHandle != IntPtr.Zero);
                _handles[i] = pc.CommHandle; // Add to array.
                _pimcContexts[i] = pc._pimcContext.Value;
                _penContexts[i] = new WeakReference(pc);
                pc = null;
            }

            // Now clean up old refs and assign new array.
            penContextRefs.Clear(); // Make sure we remove refs!
            penContextRefs = null;

            // DDVSO:167197
            // Release the PenIMC object only when we are assured that the
            // context was removed from the list of waiting handles.
            // DDVSO:474737
            // Restrict COM releases to Win7 as this can cause issues with later versions
            // of PenIMC and WISP due to using a context after it is released.
            if (removed && !OSVersionHelper.IsOsWindows8OrGreater)
            {
                Marshal.ReleaseComObject(penContext._pimcContext.Value);
            }

            return removed;
        }

        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical code (MS.Win32.Penimc.UnsafeNativeMethods.GetPenEvent, 
        ///              MS.Win32.Penimc.UnsafeNativeMethods.GetPenEventMultiple, 
        ///              MS.Win32.Penimc.UnsafeNativeMethods.DestroyResetEvent, FireEvent and FlushCache) and 
        ///              accesses SecurityCriticalData (PenContext.CommHandle and _pimcResetHandle.Value).
        ///             It is a thread proc so it is top of stack and is created by PenThreadWorker constructor.
        /// </SecurityNote>
        [SecurityCritical]
        internal void ThreadProc()
        {
            Thread.CurrentThread.Name = "Stylus Input";

            try
            {
                //
                // the rarely iterated loop
                //
                while (!__disposed)
                {
#if TRACEPTW
                    Debug.WriteLine(String.Format("PenThreadWorker::ThreadProc():  Update __penContextWeakRefList loop"));
#endif

                    WorkerOperation [] workerOps = null;

                    lock(_workerOperationLock)
                    {
                        if (_workerOperation.Count > 0)
                        {
                            workerOps = _workerOperation.ToArray();
                            _workerOperation.Clear();
                        }
                    }

                    if (workerOps != null)
                    {
                        for (int j=0; j<workerOps.Length; j++)
                        {
                            workerOps[j].DoWork();
                        }
                        workerOps = null;
                    }

                    //
                    // the intense loop of dispatching events
                    //

                    while (true)
                    {
#if TRACEPTW
                        Debug.WriteLine (String.Format("PenThreadWorker::ThreadProc - handle event loop"));
#endif
                        // get next event
                        int     evt;
                        int     stylusPointerId;
                        int     cPackets, cbPacket;
                        IntPtr  pPackets;
                        int     iHandleEvt;
                        
                        if (_handles.Length == 1)
                        {
                            if (!MS.Win32.Penimc.UnsafeNativeMethods.GetPenEvent(
                                _handles[0], _pimcResetHandle.Value,
                                out evt, out stylusPointerId,
                                out cPackets, out cbPacket, out pPackets))
                            {
                                break;
                            }
                            iHandleEvt = 0;
                        }
                        else
                        {
                            if (!MS.Win32.Penimc.UnsafeNativeMethods.GetPenEventMultiple(
                                _handles.Length, _handles, _pimcResetHandle.Value,
                                out iHandleEvt, out evt, out stylusPointerId,
                                out cPackets, out cbPacket, out pPackets))
                            {
                                break;
                            }
                        }
                        if (evt != PenEventTimeout)
                        {
                            // dispatch the event
#if TRACEPTW
                            Debug.WriteLine (String.Format("PenThreadWorker::ThreadProc - FireEvent [evt={0}, stylusId={1}]", evt, stylusPointerId));
#endif
                            // DDVSO:402947
                            // This comment addresses DDVSO:403581 which is related and likely caused by the above.
                            // This index is safe as long as there are no corruption issues within PenIMC.  There have been
                            // instances of IndexOutOfRangeExceptions from this code but this should not occur in practice.
                            // If this throws, check that the handles list generated in CPimcContext::GetPenEventMultiple
                            // is not corrupted (it has appropriate wait handles and does not point to invalid memory).
                            PenContext penContext = _penContexts[iHandleEvt].Target as PenContext;
                            // If we get an event from a GC'd PenContext then just ignore.
                            if (penContext != null)
                            {
                                FireEvent(penContext, evt, stylusPointerId, cPackets, cbPacket, pPackets);
                                penContext = null;
                            }
                        }
                        else
                        {
#if TRACEPTW
                            Debug.WriteLine (String.Format("PenThreadWorker::ThreadProc - FlushInput"));
#endif
                            FlushCache(true);

                            // we hit the timeout, make sure that all our devices are in the correct out-of-range state
                            // we are doing this to compinsate for drivers that send a move after they send a outofrange
                            for (int i = 0; i < _penContexts.Length; i++)
                            {
                                PenContext penContext = _penContexts[i].Target as PenContext;
                                if (penContext != null)
                                {
                                    // we send 0 as the stulyspointerId to trigger code in PenContext::FirePenOutOfRange
                                    penContext.FirePenOutOfRange(0, Environment.TickCount);
                                    penContext = null;
                                }
                            }
                        }
                    }
                }
            }

            finally
            {
                // Make sure we are marked as disposed now.  This keeps the
                // Dispose() method from doing any work.
                __disposed = true;

                // We've been disposed or hit thread abort.  Release this handle before exiting.
                MS.Win32.Penimc.UnsafeNativeMethods.DestroyResetEvent(_pimcResetHandle.Value);

                // Make sure the _pimcResetHandle is still valid after Dispose is called and before
                // our thread exits.
                GC.KeepAlive(this);
            }
        }
    }
}
