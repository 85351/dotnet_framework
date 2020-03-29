using System;
using System.Diagnostics;
using System.Collections;
using System.Collections.Specialized;
using System.Windows.Threading;
using System.Windows;
using System.Security;
using System.Security.Permissions;
using System.Windows.Media;
using System.Windows.Interop;
using MS.Internal;
using MS.Internal.PresentationCore;                        // SecurityHelper
using MS.Win32;
using System.Globalization;
using System.Windows.Input.StylusPlugIns;

using SR=MS.Internal.PresentationCore.SR;
using SRID=MS.Internal.PresentationCore.SRID;

namespace System.Windows.Input
{
    /////////////////////////////////////////////////////////////////////////
    /// <summary>
    ///     The StylusDevice class represents the stylus device
    /// </summary>
    public sealed class StylusDevice : InputDevice
    {
        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        /// Critical - This is code that elevates AND creates the stylus device which
        ///             happens to hold the callback to filter stylus messages
        /// TreatAsSafe: This constructor handles critical data but does not expose it
        /// </SecurityNote>
        [SecurityCritical,SecurityTreatAsSafe]
        internal StylusDevice(TabletDevice tabletDevice, string sName, int id, bool fInverted, StylusButtonCollection stylusButtonCollection)
        {
            _tabletDevice       = tabletDevice;
            _sName              = sName;
            _id                 = id;
            _fInverted          = fInverted;
            // For tablet devices that can go out of range default them to
            // being out of range until we see some events from it.
            _fInRange      = false; // All tablets out of range by default.
            _stylusButtonCollection   = stylusButtonCollection;

#if MULTICAPTURE
            _overIsEnabledChangedEventHandler = new DependencyPropertyChangedEventHandler(OnOverIsEnabledChanged);
            _overIsVisibleChangedEventHandler = new DependencyPropertyChangedEventHandler(OnOverIsVisibleChanged);
            _overIsHitTestVisibleChangedEventHandler = new DependencyPropertyChangedEventHandler(OnOverIsHitTestVisibleChanged);
            _reevaluateStylusOverDelegate = new DispatcherOperationCallback(ReevaluateStylusOverAsync);
            _reevaluateStylusOverOperation = null;

            _captureIsEnabledChangedEventHandler = new DependencyPropertyChangedEventHandler(OnCaptureIsEnabledChanged);
            _captureIsVisibleChangedEventHandler = new DependencyPropertyChangedEventHandler(OnCaptureIsVisibleChanged);
            _captureIsHitTestVisibleChangedEventHandler = new DependencyPropertyChangedEventHandler(OnCaptureIsHitTestVisibleChanged);
            _reevaluateCaptureDelegate = new DispatcherOperationCallback(ReevaluateCaptureAsync);
            _reevaluateCaptureOperation = null;
#endif

            if (_stylusButtonCollection != null)
            {
                foreach (StylusButton button in _stylusButtonCollection)
                {
                    button.SetOwner(this);
                }
            }

            // Because the stylus device gets a steady stream of input events when it is in range,
            // we don't have to be so careful about responding to layout changes as we have to be
            // with the mouse.
            // InputManager.Current.HitTestInvalidatedAsync += new EventHandler(OnHitTestInvalidatedAsync);

            InputManager inputManager = (InputManager)Dispatcher.InputManager;
            _stylusLogic = inputManager.StylusLogic;
            _stylusLogic.RegisterStylusDeviceCore(this);
        }


        /////////////////////////////////////////////////////////////////////
        ///<SecurityNote>
        /// Critical - Calls UnregisterStylusDeviceCore which can cause device
        ///             to be unusable.
        /// This could be TreatAsSafe, but it is only called by a critical method
        /// so we're leaving it as critical for now.
        ///</SecurityNote>
        [SecurityCritical]
        internal void Dispose()
        {
            _stylusLogic.UnregisterStylusDeviceCore(this);
            
            // Make sure we clean up any references that could keep our object alive.
            _inputSource = null;
            _stylusCapture = null;
            _stylusOver = null;
            _nonVerifiedTarget = null;
            _verifiedTarget = null;
            _rtiCaptureChanged = null;
            _stylusCapturePlugInCollection = null;
            _fBlockMouseMoveChanges = false;
            _tabletDevice = null;
            _stylusLogic = null;
            _fInRange = false;
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns the element that input from this device is sent to.
        /// </summary>
        public override IInputElement Target
        {
            get
            {
                VerifyAccess();
                return _stylusOver;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns whether the StylusDevice object has been internally disposed.
        /// </summary>
        public bool IsValid
        {
            get
            {
                return (_tabletDevice != null);
            }
        }

        /// <summary>
        ///     Returns the PresentationSource that is reporting input for this device.
        /// </summary>
        /// <remarks>
        ///     Callers must have UIPermission(UIPermissionWindow.AllWindows) to call this API.
        /// </remarks>
        /// <SecurityNote>
        ///     Critical - accesses critical data ( _inputSource)
        ///     PublicOK - there is a demand.
        ///                   this is safe as: 
        ///                       there is a demand for the UI permissions in the code
        /// </SecurityNote>
        public override PresentationSource ActiveSource
        {
            [SecurityCritical ]
            get
            {
                SecurityHelper.DemandUIWindowPermission();
                if (_inputSource != null)
                {
                    return _inputSource.Value;
                }
                return null;
            }
        }

        /// <summary>
        ///     Returns the PresentationSource that is reporting input for this device.
        /// </summary>
        /// <SecurityNote>
        ///     Critical - accesses critical data (_inputSource) and returns it.
        /// </SecurityNote>
        internal PresentationSource CriticalActiveSource
        {
            [SecurityCritical]
            get
            {
                if (_inputSource != null)
                {
                    return _inputSource.Value;
                }
                return null;
            }
        }

        /// <summary>
        ///     Returns the currently active PenContext (if seen) for this device.
        ///     Gets set on InRange and cleared on the out of range event (that matches PenContext).
        /// </summary>
        /// <SecurityNote>
        ///     Critical - accesses critical data (_activePenContext) and returns it.
        /// </SecurityNote>
        internal PenContext ActivePenContext
        {
            [SecurityCritical]
            get
            {
                if (_activePenContext != null)
                {
                    return _activePenContext.Value;
                }
                return null;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns the element that the stylus is over.
        /// </summary>
        internal StylusPlugInCollection CurrentNonVerifiedTarget
        {
            get
            {
                return _nonVerifiedTarget;
            }
            set
            {
                _nonVerifiedTarget = value;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns the element that the stylus is over.
        /// </summary>
        internal StylusPlugInCollection CurrentVerifiedTarget
        {
            get
            {
                return _verifiedTarget;
            }
            set
            {
                _verifiedTarget = value;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns the element that the stylus is over.
        /// </summary>
        public IInputElement DirectlyOver
        {
            get
            {
                VerifyAccess();
                return _stylusOver;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns the element that has captured the stylus.
        /// </summary>
        public IInputElement Captured
        {
            get
            {
                VerifyAccess();
                return _stylusCapture;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns the element that has captured the stylus.
        /// </summary>
        internal CaptureMode CapturedMode
        {
            get
            {
                return _captureMode;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Captures the stylus to a particular element.
        /// </summary>
        public bool Capture(IInputElement element, CaptureMode captureMode)
        {
            int timeStamp = Environment.TickCount;
            VerifyAccess();

            if (!(captureMode == CaptureMode.None || captureMode == CaptureMode.Element || captureMode == CaptureMode.SubTree))
            {
                throw new System.ComponentModel.InvalidEnumArgumentException("captureMode", (int)captureMode, typeof(CaptureMode));
            }

            if(element == null)
            {
                captureMode = CaptureMode.None;
            }

            if (captureMode == CaptureMode.None)
            {
                element = null;
            }

            // Validate that element is either a UIElement or a ContentElement
            DependencyObject doStylusCapture = element as DependencyObject;
            if (doStylusCapture != null && !InputElement.IsValid(element))
            {
                throw new InvalidOperationException(SR.Get(SRID.Invalid_IInputElement, doStylusCapture.GetType()));
            }

            if ( doStylusCapture != null )
            {
                doStylusCapture.VerifyAccess();
            }

            bool success = false;

            // The element we are capturing to must be both enabled and visible.

            UIElement e = element as UIElement;
            if (e != null)
            {
                if(e.IsVisible || e.IsEnabled)
                {
                    success = true;
                }
            }
            else
            {
                ContentElement ce = element as ContentElement;
                if (ce != null)
                {
                    if(ce.IsEnabled) // There is no IsVisible property for ContentElement
                    {
                        success = true;
                    }
                }
                else
                {
                    // Setting capture to null.
                    success = true;
                }
            }

            if(success)
            {
                ChangeStylusCapture(element, captureMode, timeStamp);
            }
            return success;
        }

        /// <summary>
        ///     Captures the stylus to a particular element.
        /// </summary>
        public bool Capture(IInputElement element)
        {
            // No need for calling ApplyTemplate since we forward the call.

            return Capture(element, CaptureMode.Element);
        }

        // called from the penthread to find out if a plugincollection has capture.
        internal StylusPlugInCollection GetCapturedPlugInCollection(ref bool elementHasCapture)
        {
            // Take lock so both are returned with proper state since called from a pen thread.
            lock(_rtiCaptureChanged)
            {
                elementHasCapture = (_stylusCapture != null);
                return _stylusCapturePlugInCollection;
            }
        }

        /// <summary>
        ///     Forces the stylusdevice to resynchronize at it's current location and state.
        ///     It can conditionally generate a Stylus Move/InAirMove (at the current location) if a change
        ///     in hittesting is detected that requires an event be generated to update elements 
        ///     to the current state (typically due to layout changes without Stylus changes).  
        ///     Has the same behavior as MouseDevice.Synchronize().
        /// </summary>
        /// <SecurityNote>
        ///     Critical - accesses SecurityCritical Data _inputSource.Value and _stylusLogic.
        ///              - calls SecurityCritical code HwndSource.CriticalHandle, 
        ///                StylusLogic.GetStylusPenContextForHwnd and 
        ///                StylusLogic.InputManagerProcessInputEventsArgs (which can be used to spoof input).
        ///     PublicOK: This code does take any inputs or outputs nor is this operation risky (no
        ///               random Stylus input can be spoofed using this API).
        /// </SecurityNote>
        [SecurityCritical]
        public void Synchronize()
        {
            // Simulate a stylus move (if we are current stylus, inrange, visuals still valid to update
            // and has moved).
            if (InRange && _inputSource != null && _inputSource.Value != null &&
                _inputSource.Value.CompositionTarget != null && !_inputSource.Value.CompositionTarget.IsDisposed)
            {
                Point ptDevice = PointUtil.ScreenToClient(_lastScreenLocation, _inputSource.Value);
                
                // GlobalHitTest always returns an IInputElement, so we are sure to have one.
                IInputElement stylusOver = GlobalHitTest(_inputSource.Value, ptDevice);
                bool fOffsetChanged = false;

                if (_stylusOver == stylusOver)
                {
                    Point ptOffset = GetPosition(stylusOver);
                    fOffsetChanged = MS.Internal.DoubleUtil.AreClose(ptOffset.X, _rawElementRelativePosition.X) == false || MS.Internal.DoubleUtil.AreClose(ptOffset.Y, _rawElementRelativePosition.Y) == false;
                }

                if(fOffsetChanged || _stylusOver != stylusOver)
                {
                    int timeStamp = Environment.TickCount;
                    PenContext penContext = _stylusLogic.GetStylusPenContextForHwnd(_inputSource.Value,TabletDevice.Id);

                    if (_eventStylusPoints != null &&
                        _eventStylusPoints.Count > 0 &&
                        StylusPointDescription.AreCompatible(penContext.StylusPointDescription, _eventStylusPoints.Description))
                    {

                        StylusPoint stylusPoint = _eventStylusPoints[_eventStylusPoints.Count - 1];
                        int[] data = stylusPoint.GetPacketData();

                        // get back to the correct coordinate system
                        Matrix m = TabletDevice.TabletToScreen;
                        m.Invert();
                        Point ptTablet = ptDevice * m;

                        data[0] = (int)ptTablet.X;
                        data[1] = (int)ptTablet.Y;

                        RawStylusInputReport report = new RawStylusInputReport(InputMode.Foreground,
                                                                             timeStamp,
                                                                             _inputSource.Value,
                                                                             penContext,
                                                                             InAir?RawStylusActions.InAirMove:RawStylusActions.Move,
                                                                             TabletDevice.Id,
                                                                             Id,
                                                                             data);


                        report.Synchronized = true;

                        InputReportEventArgs inputReportEventArgs = new InputReportEventArgs(this, report);
                        inputReportEventArgs.RoutedEvent=InputManager.PreviewInputReportEvent;

                        _stylusLogic.InputManagerProcessInputEventArgs(inputReportEventArgs);
                    }
                }
            }
        }

#if MULTICAPTURE
        private void UpdateOverProperty(IInputElement oldOver, IInputElement newOver)
        {
            DependencyObject o = null;

            // Adjust the handlers we use to track everything.
            if (oldOver != null)
            {
                o = oldOver as DependencyObject;
                if (InputElement.IsUIElement(o))
                {
                    ((UIElement)o).IsEnabledChanged -= _overIsEnabledChangedEventHandler;
                    ((UIElement)o).IsVisibleChanged -= _overIsVisibleChangedEventHandler;
                    ((UIElement)o).IsHitTestVisibleChanged -= _overIsHitTestVisibleChangedEventHandler;
                }
                else if (InputElement.IsContentElement(o))
                {
                    ((ContentElement)o).IsEnabledChanged -= _overIsEnabledChangedEventHandler;

                    // NOTE: there are no IsVisible or IsHitTestVisible properties for ContentElements.
                    //
                    // ((ContentElement)o).IsVisibleChanged -= _overIsVisibleChangedEventHandler;
                    // ((ContentElement)o).IsHitTestVisibleChanged -= _overIsHitTestVisibleChangedEventHandler;
                }
                else
                {
                    ((UIElement3D)o).IsEnabledChanged -= _overIsEnabledChangedEventHandler;
                    ((UIElement3D)o).IsVisibleChanged -= _overIsVisibleChangedEventHandler;
                    ((UIElement3D)o).IsHitTestVisibleChanged -= _overIsHitTestVisibleChangedEventHandler;
                }
            }
            if (_stylusOver != null)
            {
                o = _stylusOver as DependencyObject;
                if (InputElement.IsUIElement(o))
                {
                    ((UIElement)o).IsEnabledChanged += _overIsEnabledChangedEventHandler;
                    ((UIElement)o).IsVisibleChanged += _overIsVisibleChangedEventHandler;
                    ((UIElement)o).IsHitTestVisibleChanged += _overIsHitTestVisibleChangedEventHandler;
                }
                else if (InputElement.IsContentElement(o))
                {
                    ((ContentElement)o).IsEnabledChanged += _overIsEnabledChangedEventHandler;

                    // NOTE: there are no IsVisible or IsHitTestVisible properties for ContentElements.
                    //
                    // ((ContentElement)o).IsVisibleChanged += _overIsVisibleChangedEventHandler;
                    // ((ContentElement)o).IsHitTestVisibleChanged += _overIsHitTestVisibleChangedEventHandler;
                }
                else
                {
                    ((UIElement3D)o).IsEnabledChanged += _overIsEnabledChangedEventHandler;
                    ((UIElement3D)o).IsVisibleChanged += _overIsVisibleChangedEventHandler;
                    ((UIElement3D)o).IsHitTestVisibleChanged += _overIsHitTestVisibleChangedEventHandler;
                }
            }

            // Oddly enough, update the IsStylusOver property first.  This is
            // so any callbacks will see the more-common IsStylusOver property
            // set correctly.
            UIElement.StylusOverProperty.OnOriginValueChanged(oldOver as DependencyObject, _stylusOver as DependencyObject, ref _stylusOverTreeState);

            // Invalidate the IsStylusDirectlyOver property.
            if (oldOver != null)
            {
                o = oldOver as DependencyObject;
                o.SetValue(UIElement.IsStylusDirectlyOverPropertyKey, false); // Same property for ContentElements
            }
            if (_stylusOver != null)
            {
                o = _stylusOver as DependencyObject;
                o.SetValue(UIElement.IsStylusDirectlyOverPropertyKey, true); // Same property for ContentElements
            }
        }


        private void OnOverIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The element that the stylus is over just became disabled.
            //
            // We need to resynchronize the stylus so that we can figure out who
            // the stylus is over now.

            ReevaluateStylusOver(null, null, true);
        }

        private void OnOverIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The element that the stylus is over just became non-visible (collapsed or hidden).
            //
            // We need to resynchronize the stylus so that we can figure out who
            // the stylus is over now.

            ReevaluateStylusOver(null, null, true);
        }

        private void OnOverIsHitTestVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The element that the stylus is over was affected by a change in hit-test visibility.
            //
            // We need to resynchronize the stylus so that we can figure out who
            // the stylus is over now.

            ReevaluateStylusOver(null, null, true);
        }

        /// <summary>
        /// </summary>
        internal void ReevaluateStylusOver(DependencyObject element, DependencyObject oldParent, bool isCoreParent)
        {
            if (element != null)
            {
                if (isCoreParent)
                {
                    StylusOverTreeState.SetCoreParent(element, oldParent);
                }
                else
                {
                    StylusOverTreeState.SetLogicalParent(element, oldParent);
                }
            }

            // It would be best to re-evaluate anything dependent on the hit-test results
            // immediately after layout & rendering are complete.  Unfortunately this can
            // lead to an infinite loop.  Consider the following scenario:
            //
            // If the stylus is over an element, hide it.
            //
            // This never resolves to a "correct" state.  When the stylus moves over the
            // element, the element is hidden, so the stylus is no longer over it, so the
            // element is shown, but that means the stylus is over it again.  Repeat.
            //
            // We push our re-evaluation to a priority lower than input processing so that
            // the user can change the input device to avoid the infinite loops, or close
            // the app if nothing else works.
            //
            if (_reevaluateStylusOverOperation == null)
            {
                _reevaluateStylusOverOperation = Dispatcher.BeginInvoke(DispatcherPriority.Input, _reevaluateStylusOverDelegate, null);
            }
        }

        /// <summary>
        /// </summary>
        /// <param name="arg"></param>
        /// <returns></returns>
        private object ReevaluateStylusOverAsync(object arg)
        {
            _reevaluateStylusOverOperation = null;

            // Synchronize causes state issues with the stylus events so we don't do this.
            //if (_currentStylusDevice != null)
            //{
            //    _currentStylusDevice.Synchronize();
            //}

            // Refresh StylusOverProperty so that ReverseInherited Flags are updated.
            //
            // We only need to do this is there is any information about the old
            // tree state.  This is because it is possible (even likely) that
            // Synchronize() would have already done this if we hit-tested to a
            // different element.
            if (_stylusOverTreeState != null && !_stylusOverTreeState.IsEmpty)
            {
                UIElement.StylusOverProperty.OnOriginValueChanged(_stylusOver as DependencyObject, _stylusOver as DependencyObject, ref _stylusOverTreeState);
            }

            return null;
        }
#endif

        /////////////////////////////////////////////////////////////////////

        // NOTE: This will typically get called for each stylus device on the
        //       system since Stylus.Capture will enumerate them all and call
        //       capture.
        /// <SecurityNote>
        ///     Critical: Calls SecurityCritical code (PresentationSource.CriticalFromVisual, 
        ///                StylusLogic.GetPenContextsFromHwndand and StylusLogic.InputManagerProcessInputEventArgs).
        ///               Accesses SecurityCriticalData (_stylusLogic).
        ///     TreatAsSafe: This operation is ok to expose since stylus capture is ok. Even if you get the
        ///               source changed events you cannot get to the sources themselves
        /// </SecurityNote>
        [SecurityCritical,SecurityTreatAsSafe]
#if MULTICAPTURE
        private void ChangeStylusCapture(IInputElement stylusCapture, CaptureMode captureMode, int timestamp)
#else
        internal void ChangeStylusCapture(IInputElement stylusCapture, CaptureMode captureMode, int timestamp)
#endif
        {
            // if the capture changed...
            if(stylusCapture != _stylusCapture)
            {
                // Actually change the capture first.  Invalidate the properties,
                // and then send the events.
                IInputElement oldStylusCapture = _stylusCapture;
                using(Dispatcher.DisableProcessing()) // Disable reentrancy due to locks taken
                {
                    lock(_rtiCaptureChanged)
                    {
                        _stylusCapture = stylusCapture;
                        _captureMode = captureMode;

                        // We also need to figure out ahead of time if any plugincollections on this captured element (or a parent)
                        // for the penthread hittesting code.
                        _stylusCapturePlugInCollection = null;
                        if (stylusCapture != null)
                        {
                            UIElement uiElement = InputElement.GetContainingUIElement(stylusCapture as DependencyObject) as UIElement;
                            if (uiElement != null)
                            {
                                PresentationSource source = PresentationSource.CriticalFromVisual(uiElement as Visual);
                                                      
                                if (source != null)
                                {
                                    PenContexts penContexts = _stylusLogic.GetPenContextsFromHwnd(source);

                                    _stylusCapturePlugInCollection = penContexts.FindPlugInCollection(uiElement);
                                }
                            }
                        }
                    }
                }

#if MULTICAPTURE
                DetachFromPropertiesAffectingCapture(oldStylusCapture);
                AttachToPropertiesAffectingCapture(_stylusCapture);
                
                // Oddly enough, update the IsStylusCaptureWithin property first.  This is
                // so any callbacks will see the more-common IsStylusCaptureWithin property
                // set correctly.
                UIElement.StylusCaptureWithinProperty.OnOriginValueChanged(oldStylusCapture as DependencyObject, _stylusCapture as DependencyObject, ref _stylusCaptureWithinTreeState);
                
                // Invalidate the IsStylusCaptured properties.
                if (oldStylusCapture != null)
                {
                    var o = oldStylusCapture as DependencyObject;
                    o.SetValue(UIElement.IsStylusCapturedPropertyKey, false); // Same property for ContentElements
                }
                if (_stylusCapture != null)
                {
                    var o = _stylusCapture as DependencyObject;
                    o.SetValue(UIElement.IsStylusCapturedPropertyKey, true); // Same property for ContentElements
                }
#else
                _stylusLogic.UpdateStylusCapture(this, oldStylusCapture, _stylusCapture, timestamp);
#endif

                // Send the LostStylusCapture and GotStylusCapture events.
                if(oldStylusCapture != null)
                {
                    StylusEventArgs lostCapture = new StylusEventArgs(this, timestamp);
                    lostCapture.RoutedEvent=Stylus.LostStylusCaptureEvent;
                    lostCapture.Source= oldStylusCapture;
                    _stylusLogic.InputManagerProcessInputEventArgs(lostCapture);
                }
                if(_stylusCapture != null)
                {
                    StylusEventArgs gotCapture = new StylusEventArgs(this, timestamp);
                    gotCapture.RoutedEvent=Stylus.GotStylusCaptureEvent;
                    gotCapture.Source= _stylusCapture;
                    _stylusLogic.InputManagerProcessInputEventArgs(gotCapture);
                }

                // Now update the stylus over state (only if this is the current stylus and 
                // it is inrange).
                if (_stylusLogic.CurrentStylusDevice == this || InRange)
                {
                    if (_stylusCapture != null)
                    {
                        IInputElement inputElementHit = _stylusCapture;

                        // See if we need to update over for subtree mode.
                        if (CapturedMode == CaptureMode.SubTree && _inputSource != null && _inputSource.Value != null)
                        {
                            Point pt = _stylusLogic.DeviceUnitsFromMeasureUnits(GetPosition(null));
                            inputElementHit = FindTarget(_inputSource.Value, pt);
                        }

                        ChangeStylusOver(inputElementHit);
                    }
                    else
                    {
                        // Only try to update over if we have a valid input source.
                        if (_inputSource != null && _inputSource.Value != null)
                        {
                            Point pt = GetPosition(null); // relative to window (root element)
                            pt = _stylusLogic.DeviceUnitsFromMeasureUnits(pt); // change back to device coords.
                            IInputElement currentOver = GlobalHitTest(_inputSource.Value, pt);
                            ChangeStylusOver(currentOver);
                        }
                    }
                }


                // For Mouse StylusDevice we want to make sure Mouse capture is set up the same.
#if MULTICAPTURE
                if ((Mouse.PrimaryDevice.StylusDevice == this) && (Mouse.Captured != _stylusCapture || Mouse.CapturedMode != _captureMode))
#else
                if (Mouse.Captured != _stylusCapture || Mouse.CapturedMode != _captureMode)
#endif
                {
                    Mouse.Capture(_stylusCapture, _captureMode);
                }
            }
        }

#if MULTICAPTURE
        internal void AttachToPropertiesAffectingCapture(IInputElement element)
        {
            // Adjust the handlers we use to track everything.
            if (element != null)
            {
                var o = element as DependencyObject;
                if (InputElement.IsUIElement(o))
                {
                    ((UIElement)o).IsEnabledChanged += _captureIsEnabledChangedEventHandler;
                    ((UIElement)o).IsVisibleChanged += _captureIsVisibleChangedEventHandler;
                    ((UIElement)o).IsHitTestVisibleChanged += _captureIsHitTestVisibleChangedEventHandler;
                }
                else if (InputElement.IsContentElement(o))
                {
                    ((ContentElement)o).IsEnabledChanged += _captureIsEnabledChangedEventHandler;

                    // NOTE: there are no IsVisible or IsHitTestVisible properties for ContentElements.
                    //
                    // ((ContentElement)o).IsVisibleChanged += _captureIsVisibleChangedEventHandler;
                    // ((ContentElement)o).IsHitTestVisibleChanged += _captureIsHitTestVisibleChangedEventHandler;
                }
                else
                {
                    ((UIElement3D)o).IsEnabledChanged += _captureIsEnabledChangedEventHandler;
                    ((UIElement3D)o).IsVisibleChanged += _captureIsVisibleChangedEventHandler;
                    ((UIElement3D)o).IsHitTestVisibleChanged += _captureIsHitTestVisibleChangedEventHandler;
                }
            }
        }

        internal void DetachFromPropertiesAffectingCapture(IInputElement element)
        {
            if (element != null)
            {
                var o = element as DependencyObject;
                if (InputElement.IsUIElement(o))
                {
                    ((UIElement)o).IsEnabledChanged -= _captureIsEnabledChangedEventHandler;
                    ((UIElement)o).IsVisibleChanged -= _captureIsVisibleChangedEventHandler;
                    ((UIElement)o).IsHitTestVisibleChanged -= _captureIsHitTestVisibleChangedEventHandler;
                }
                else if (InputElement.IsContentElement(o))
                {
                    ((ContentElement)o).IsEnabledChanged -= _captureIsEnabledChangedEventHandler;

                    // NOTE: there are no IsVisible or IsHitTestVisible properties for ContentElements.
                    //
                    // ((ContentElement)o).IsVisibleChanged -= _captureIsVisibleChangedEventHandler;
                    // ((ContentElement)o).IsHitTestVisibleChanged -= _captureIsHitTestVisibleChangedEventHandler;
                }
                else
                {
                    ((UIElement3D)o).IsEnabledChanged -= _captureIsEnabledChangedEventHandler;
                    ((UIElement3D)o).IsVisibleChanged -= _captureIsVisibleChangedEventHandler;
                    ((UIElement3D)o).IsHitTestVisibleChanged -= _captureIsHitTestVisibleChangedEventHandler;
                }
            }
        }

        private void OnCaptureIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The element that the stylus is captured to just became disabled.
            //
            // We need to re-evaluate the element that has stylus capture since
            // we can't allow the stylus to remain captured by a disabled element.

            ReevaluateCapture(null, null, true);
        }

        private void OnCaptureIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The element that the stylus is captured to just became non-visible (collapsed or hidden).
            //
            // We need to re-evaluate the element that has stylus capture since
            // we can't allow the stylus to remain captured by a non-visible element.

            ReevaluateCapture(null, null, true);
        }

        private void OnCaptureIsHitTestVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The element that the stylus is captured to was affected by a change in hit-test visibility.
            //
            // We need to re-evaluate the element that has stylus capture since
            // we can't allow the stylus to remain captured by a non-hittest-visible element.

            ReevaluateCapture(null, null, true);
        }

        /// <summary>
        /// </summary>
        internal void ReevaluateCapture(DependencyObject element, DependencyObject oldParent, bool isCoreParent)
        {
            if (element != null)
            {
                if (isCoreParent)
                {
                    StylusCaptureWithinTreeState.SetCoreParent(element, oldParent);
                }
                else
                {
                    StylusCaptureWithinTreeState.SetLogicalParent(element, oldParent);
                }
            }

            // We re-evaluate the captured element to be consistent with how
            // we re-evaluate the element the stylus is over.
            //
            // See ReevaluateStylusOver for details.
            //
            if (_reevaluateCaptureOperation == null)
            {
                _reevaluateCaptureOperation = Dispatcher.BeginInvoke(DispatcherPriority.Input, _reevaluateCaptureDelegate, null);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="arg"></param>
        /// <returns></returns>
        private object ReevaluateCaptureAsync(object arg)
        {
            _reevaluateCaptureOperation = null;

            if (_stylusCapture == null)
                return null;

            bool killCapture = false;

            DependencyObject dependencyObject = _stylusCapture as DependencyObject;

            //
            // First, check things like IsEnabled, IsVisible, etc. on a
            // UIElement vs. ContentElement basis.
            //
            if (InputElement.IsUIElement(dependencyObject))
            {
                killCapture = !ValidateUIElementForCapture((UIElement)_stylusCapture);
            }
            else if (InputElement.IsContentElement(dependencyObject))
            {
                killCapture = !ValidateContentElementForCapture((ContentElement)_stylusCapture);
            }
            else
            {
                killCapture = !ValidateUIElement3DForCapture((UIElement3D)_stylusCapture);
            }

            //
            // Second, if we still haven't thought of a reason to kill capture, validate
            // it on a Visual basis for things like still being in the right tree.
            //
            if (killCapture == false)
            {
                DependencyObject containingVisual = InputElement.GetContainingVisual(dependencyObject);
                killCapture = !ValidateVisualForCapture(containingVisual);
            }

            //
            // Lastly, if we found any reason above, kill capture.
            //
            if (killCapture)
            {
                Stylus.Capture(null);
            }

            // Refresh StylusCaptureWithinProperty so that ReverseInherited flags are updated.
            //
            // We only need to do this is there is any information about the old
            // tree state.  This is because it is possible (even likely) that
            // we would have already killed capture if the capture criteria was
            // no longer met.
            if (_stylusCaptureWithinTreeState != null && !_stylusCaptureWithinTreeState.IsEmpty)
            {
                UIElement.StylusCaptureWithinProperty.OnOriginValueChanged(_stylusCapture as DependencyObject, _stylusCapture as DependencyObject, ref _stylusCaptureWithinTreeState);
            }

            return null;
        }

        private static bool ValidateUIElementForCapture(UIElement element)
        {
            if (element.IsEnabled == false)
                return false;

            if (element.IsVisible == false)
                return false;

            if (element.IsHitTestVisible == false)
                return false;

            return true;
        }

        private static bool ValidateContentElementForCapture(ContentElement element)
        {
            if (element.IsEnabled == false)
                return false;

            // NOTE: there is no IsVisible property for ContentElements.

            return true;
        }

        private static bool ValidateUIElement3DForCapture(UIElement3D element)
        {
            if (element.IsEnabled == false)
                return false;

            if (element.IsVisible == false)
                return false;

            if (element.IsHitTestVisible == false)
                return false;

            return true;
        }

        /// <SecurityNote>
        ///     Critical: This code accesses critical data (PresentationSource) 
        ///     TreatAsSafe: Although it accesses critical data it does not modify or expose it, only compares against it.
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private bool ValidateVisualForCapture(DependencyObject visual)
        {
            if (visual == null)
                return false;

            PresentationSource presentationSource = PresentationSource.CriticalFromVisual(visual);

            if (presentationSource == null)
            {
                return false;
            }

            if (CriticalActiveSource != presentationSource &&
                Captured == null)
            {
                return false;
            }

            return true;
        }
#endif

        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        /// Critical - Accesses SecurityCriticalData _stylusLogic.
        ///     TreatAsSafe: This code does not expose the PresentationSource and simply changes the stylus over element
        /// </SecurityNote>
        [SecurityCritical,SecurityTreatAsSafe]
        internal void ChangeStylusOver(IInputElement stylusOver)
        {
            // We are not syncing the OverSourceChanged event
            // the reasons for doing so are listed in the MouseDevice.cs OnOverSourceChanged implementation
            if(_stylusOver != stylusOver)
            {
#if MULTICAPTURE
                var oldStylusOver = _stylusOver;
#endif
                _stylusOver = stylusOver;
                _rawElementRelativePosition = GetPosition(_stylusOver);
#if MULTICAPTURE
                UpdateOverProperty(oldStylusOver, _stylusOver);
#endif
            }
            else
            {
                // Always update the relative position if InRange since ChangeStylusOver is only
                // called when something changed (like capture or stylus moved) and in
                // that case we want this updated properly.  This value is used in Synchronize().
                if (InRange)
                {
                    _rawElementRelativePosition = GetPosition(_stylusOver);
                }
            }

#if !MULTICAPTURE
            // The stylus over property is a singleton (only one stylus device at a time can
            // be over an element) so we let StylusLogic manager the element over state. 
            // NOTE: StylusLogic only allows the CurrentStylusDevice to change the over state.
            // Also note that Capture is also managed by StylusLogic in a similar fashion.
            _stylusLogic.UpdateOverProperty(this, _stylusOver);
#endif
        }


        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical: - Uses security critical data (inputSource)
        ///                - TreatAsSafe boundry at ChangeStylusCapture
        ///                - called by ChangeStylusCapture
        /// </SecurityNote>
        [SecurityCritical]
        internal IInputElement FindTarget(PresentationSource inputSource, Point position)
        {
            IInputElement stylusOver = null;

            switch (_captureMode)
            {
                case CaptureMode.None:
                    {
                        stylusOver = GlobalHitTest(inputSource, position);

                        // We understand UIElements and ContentElements.
                        // If we are over something else (like a raw visual)
                        // find the containing element.
                        if (!InputElement.IsValid(stylusOver))
                            stylusOver = InputElement.GetContainingInputElement(stylusOver as DependencyObject);
                    }
                    break;

                case CaptureMode.Element:
                    // 
                    stylusOver = _stylusCapture;
                    break;

                case CaptureMode.SubTree:
                    {
                        IInputElement stylusCapture = InputElement.GetContainingInputElement(_stylusCapture as DependencyObject);

                        if ( stylusCapture != null && inputSource != null )
                        {
                            // We need to re-hit-test to get the "real" UIElement we are over.
                            // This allows us to have our capture-to-subtree span multiple windows.

                            // GlobalHitTest always returns an IInputElement, so we are sure to have one.
                            stylusOver = GlobalHitTest(inputSource, position);
                        }

                        if (stylusOver != null && !InputElement.IsValid(stylusOver))
                            stylusOver = InputElement.GetContainingInputElement(stylusOver as DependencyObject);

                        // Make sure that the element we hit is acutally underneath
                        // our captured element.  Because we did a global hit test, we
                        // could have hit an element in a completely different window.
                        //
                        // Note that we support the child being in a completely different window.
                        // So we use the GetUIParent method instead of just looking at
                        // visual/content parents.
                        if (stylusOver != null)
                        {
                            IInputElement ieTest = stylusOver;
                            UIElement eTest = null;
                            ContentElement ceTest = null;

                            while (ieTest != null && ieTest != stylusCapture)
                            {
                                eTest = ieTest as UIElement;

                                if(eTest != null)
                                {
                                    ieTest = InputElement.GetContainingInputElement(eTest.GetUIParent(true));
                                }
                                else
                                {
                                    ceTest = ieTest as ContentElement; // Should never fail.

                                    ieTest = InputElement.GetContainingInputElement(ceTest.GetUIParent(true));
                                }
                            }

                            // If we missed the capture point, we didn't hit anything.
                            if (ieTest != stylusCapture)
                            {
                                stylusOver = _stylusCapture;
                            }
                        }
                        else
                        {
                            // We didn't hit anything.  Consider the stylus over the capture point.
                            stylusOver = _stylusCapture;
                        }
                    }
                    break;
            }

            return stylusOver;
        }

        /////////////////////////////////////////////////////////////////////

        internal static IInputElement LocalHitTest(PresentationSource inputSource, Point pt)
        {
            return MouseDevice.LocalHitTest(pt, inputSource);
        }


        /////////////////////////////////////////////////////////////////////

        internal static IInputElement GlobalHitTest(PresentationSource inputSource, Point pt)
        {
            return MouseDevice.GlobalHitTest(pt, inputSource);
        }


        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Returns the tablet associated with the StylusDevice
        /// </summary>
        public TabletDevice TabletDevice
        {
            get
            {
                // Don't do the VerifyAccess call any more since we need to call this prop
                // from the pen thread to get access to internal data.  The TabletDevice 
                // is already a DispatcherObject so it will do VerifyAccess() on any 
                // methods called on the wrong thread.
                // VerifyAccess();
                return _tabletDevice;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Returns the name of the StylusDevice
        /// </summary>
        public string Name
        {
            get
            {
                VerifyAccess();
                return _sName;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Returns the friendly representation of the StylusDevice
        /// </summary>
        public override string ToString()
        {
            return String.Format(CultureInfo.CurrentCulture, "{0}({1})", base.ToString(), this.Name);
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Returns the hardware id of the StylusDevice
        /// </summary>
        public int Id
        {
            get
            {
                VerifyAccess();
                return _id;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns a StylusPointCollection object for processing the data in the packet.
        ///     This method creates a new StylusPointCollection and copies the data.
        /// </summary>
        public StylusPointCollection GetStylusPoints(IInputElement relativeTo)
        {
            VerifyAccess();

            // Fake up an empty one if we have to.
            if (_eventStylusPoints == null)
            {
                return new StylusPointCollection(TabletDevice.StylusPointDescription);
            }
            return _eventStylusPoints.Clone(GetElementTransform(relativeTo), _eventStylusPoints.Description);
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Returns a StylusPointCollection object for processing the data in the packet.
        ///     This method creates a new StylusPointCollection and copies the data.
        /// </summary>
        public StylusPointCollection GetStylusPoints(IInputElement relativeTo, StylusPointDescription subsetToReformatTo)
        {
            if (null == subsetToReformatTo)
            {
                throw new ArgumentNullException("subsetToReformatTo");
            }
            // Fake up an empty one if we have to.
            if (_eventStylusPoints == null)
            {
                return new StylusPointCollection(subsetToReformatTo);
            }

            return _eventStylusPoints.Reformat(subsetToReformatTo, GetElementTransform(relativeTo));
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Returns the button collection that is associated with the StylusDevice.
        /// </summary>
        public StylusButtonCollection StylusButtons
        {
            get
            {
                VerifyAccess();
                return _stylusButtonCollection;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Calculates the position of the stylus relative to a particular element.
        /// </summary>
        ///<SecurityNote>
        ///     Critical - accesses critical data _inputSource.Value
        ///     PublicOK - we do the elevation of _inputSource to get RootVisual.
        ///</SecurityNote>
        [SecurityCritical]
        public Point GetPosition(IInputElement relativeTo)
        {
            VerifyAccess();
            
            // Validate that relativeTo is either a UIElement or a ContentElement
            if (relativeTo != null && !InputElement.IsValid(relativeTo))
            {
                throw new InvalidOperationException(SR.Get(SRID.Invalid_IInputElement, relativeTo.GetType()));
            }
            
            PresentationSource relativePresentationSource = null;
            
            if (relativeTo != null)
            {
                DependencyObject dependencyObject = relativeTo as  DependencyObject;
                DependencyObject containingVisual = InputElement.GetContainingVisual(dependencyObject);
                if(containingVisual != null)
                {
                    relativePresentationSource = PresentationSource.CriticalFromVisual(containingVisual);
                }
            }
            else
            {
                if (_inputSource != null)
                {
                    relativePresentationSource = _inputSource.Value;
                }
            }

            // Verify that we have a valid PresentationSource with a valid RootVisual
            // - if we don't we won't be able to invoke ClientToRoot or TranslatePoint and 
            //   we will just return 0,0
            if (relativePresentationSource == null || relativePresentationSource.RootVisual == null)
            {
                return new Point(0, 0);
            }

            Point ptClient = PointUtil.ScreenToClient(_lastScreenLocation, relativePresentationSource);
            Point ptRoot      = PointUtil.ClientToRoot(ptClient, relativePresentationSource);
            Point ptRelative  = InputElement.TranslatePoint(ptRoot, relativePresentationSource.RootVisual, (DependencyObject)relativeTo);

            return ptRelative;
            
        }

        /// <summary>
        /// This will return the same result as GetPosition if the packet data points
        /// are not modified in the StylusPlugIns, otherwise it will return the unmodified
        /// data
        /// </summary>
        /// <param name="relativeTo"></param>
        /// <returns></returns>
        internal Point GetRawPosition(IInputElement relativeTo)
        {
            GeneralTransform transform = GetElementTransform(relativeTo);
            Point pt;
            transform.TryTransform((Point)_rawPosition, out pt);
            return pt;
        }

        internal StylusPoint RawStylusPoint
        {
            get { return _rawPosition; }
        }


        /// <summary>
        ///     Gets the current state of the specified button
        /// </summary>
        /// <param name="mouseButton">
        ///     The mouse button to get the state of
        /// </param>
        /// <param name="mouseDevice">
        ///     The MouseDevice that is making the request
        /// </param>
        /// <returns>
        ///     The state of the specified mouse button
        /// </returns>
        /// <remarks>
        ///     This is the hook where the Input system (via the MouseDevice) can call back into
        ///     the Stylus system when we are processing Stylus events instead of Mouse events
        /// </remarks>
        /// <SecurityNote>
        /// Critical:   References SecurityCriticalData _stylusLogic.
        /// TreatAsSafe: Takes no securityCriticalInput and returns safe data (MouseButtonState).
        ///                Called by MouseDevice for StylusDevice promoted mouse events to query
        ///                the mouse button state that should be reported.
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        internal MouseButtonState GetMouseButtonState(MouseButton mouseButton, MouseDevice mouseDevice)
        {
            if (mouseButton == MouseButton.Left)
            {
                return _stylusLogic.GetMouseLeftOrRightButtonState(true);
            }
            if (mouseButton == MouseButton.Right)
            {
                return _stylusLogic.GetMouseLeftOrRightButtonState(false);
            }

            //       can defer back to the mouse device that called you and it will call Win32
            return mouseDevice.GetButtonStateFromSystem(mouseButton);
        }

        /// <summary>
        ///     Gets the current position of the mouse in screen co-ords
        /// </summary>
        /// <param name="mouseDevice">
        ///     The MouseDevice that is making the request
        /// </param>
        /// <returns>
        ///     The current mouse location in screen co-ords
        /// </returns>
        /// <remarks>
        ///     This is the hook where the Input system (via the MouseDevice) can call back into
        ///     the Stylus system when we are processing Stylus events instead of Mouse events
        /// </remarks>
        internal Point GetMouseScreenPosition(MouseDevice mouseDevice)
        {
            if (mouseDevice == null)
            {
                // return the last location this stylus device promoted a mouse for.
                return _lastMouseScreenLocation;
            }
            else
            {
                // The mouse device now caches the last location seen from the last input
                // report so we can just call back to them to get the location.  We don't
                // need to return our cached location currrently.
                return mouseDevice.GetScreenPositionFromSystem();
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        /// <param name="relativeTo"></param>
        /// <ExternalAPI/>
        internal static GeneralTransform GetElementTransform(IInputElement relativeTo)
        {
            GeneralTransform elementTransform = Transform.Identity;
            DependencyObject doRelativeTo = relativeTo as DependencyObject;

            if (doRelativeTo != null)
            {
                Visual visualFirstAncestor = VisualTreeHelper.GetContainingVisual2D(InputElement.GetContainingVisual(doRelativeTo));
                Visual visualRoot          = VisualTreeHelper.GetContainingVisual2D(InputElement.GetRootVisual      (doRelativeTo));

                GeneralTransform g = visualRoot.TransformToDescendant(visualFirstAncestor);
                if (g != null)
                {
                    elementTransform = g;
                }
            }

            return elementTransform;
        }

        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical - accesses critical member _stylusLogic
        /// </SecurityNote>
        /// <summary>
        ///     Returns the transform for converting from tablet to element
        ///     relative coordinates.
        /// </summary>
        [SecurityCritical]
        private GeneralTransform GetTabletToElementTransform(IInputElement relativeTo)
        {
            GeneralTransformGroup group = new GeneralTransformGroup();
            group.Children.Add(new MatrixTransform(_stylusLogic.GetTabletToViewTransform(TabletDevice)));
            group.Children.Add(GetElementTransform(relativeTo));
            return group;
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Indicates the stylus is not touching the surface.
        ///     InAir events are general sent at a lower frequency.
        /// </summary>
        public bool InAir
        {
            get
            {
                VerifyAccess();
                return _fInAir;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Indicates stylusDevice is in the inverted state.
        /// </summary>
        public bool Inverted
        {
            get
            {
                VerifyAccess();
                return _fInverted;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        ///     Indicates stylusDevice is in the inverted state.
        /// </summary>
        public bool InRange
        {
            get
            {
                VerifyAccess();
                return _fInRange;
            }
        }

        /////////////////////////////////////////////////////////////////////

        /// <SecurityNote>
        ///     Critical - Can be used to spoof input.
        ///      At the top called from StylusLogic::PostProcessInput event which is SecurityCritical
        /// </SecurityNote>
        [SecurityCritical]
        internal void UpdateEventStylusPoints(RawStylusInputReport report, bool resetIfNoOverride)
        {
            if (report.RawStylusInput != null && report.RawStylusInput.StylusPointsModified)
            {
                GeneralTransform transformToElement = report.RawStylusInput.Target.ViewToElement.Inverse;                
                //note that RawStylusInput.Target (of type StylusPluginCollection)
                //guarantees that ViewToElement is invertible
                Debug.Assert(transformToElement != null);
                
                _eventStylusPoints = report.RawStylusInput.GetStylusPoints(transformToElement);
            }
            else if (resetIfNoOverride)
            {
                _eventStylusPoints =
                    new StylusPointCollection(report.StylusPointDescription,
                                              report.GetRawPacketData(),
                                              GetTabletToElementTransform(null),
                                              Matrix.Identity);
            }
        }

        internal int TapCount
        {
            get {return _tapCount;}
            set { _tapCount = value;}
        }

        internal int LastTapTime
        {
            get {return _lastTapTime;}
            set { _lastTapTime = value;}
        }

        internal Point LastTapPoint
        {
            get {return _lastTapXY;}
            set { _lastTapXY = value;}
        }

        internal bool LastTapBarrelDown
        {
            get {return _lastTapBarrelDown;}
            set { _lastTapBarrelDown = value;}
        }

        internal int DoubleTapDeltaX
        {
            get {return (int)TabletDevice.DoubleTapSize.Width;}
        }

        internal int DoubleTapDeltaY
        {
            get {return (int)TabletDevice.DoubleTapSize.Height;}
        }

        /// <SecurityNote>
        /// Critical - Calls SecurityCritical method StylusLogic.CurrentlStylusLogic.
        /// TreatAsSafe: Takes no input and returns safe data (double tap info - time delta for double tap).
        /// </SecurityNote>
        internal int DoubleTapDeltaTime
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get {return _stylusLogic.DoubleTapDeltaTime;}
        }

        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical: creates SecurityCriticalData (_inputSource)
        ///                - Called from the StylusLogic::PreProcessInput event handler
        /// </SecurityNote>
        [SecurityCritical]
        internal void UpdateState(RawStylusInputReport report)
        {
            Debug.Assert(report.TabletDeviceId == _tabletDevice.Id);
            Debug.Assert((report.Actions & RawStylusActions.None) == 0);

            _eventStylusPoints =
                new StylusPointCollection(  report.StylusPointDescription,
                                            report.GetRawPacketData(),
                                            GetTabletToElementTransform(null),
                                            Matrix.Identity);

            PresentationSource inputSource = DetermineValidSource(report.InputSource, _eventStylusPoints, report.PenContext.Contexts);
            
            // See if we need to remap the stylus data X and Y values to different presentation source.
            if (inputSource != null && inputSource != report.InputSource)
            {
                Point newWindowLocation = PointUtil.ClientToScreen(new Point(0, 0), inputSource);
                newWindowLocation = _stylusLogic.MeasureUnitsFromDeviceUnits(newWindowLocation);
                Point oldWindowLocation = _stylusLogic.MeasureUnitsFromDeviceUnits(report.PenContext.Contexts.DestroyedLocation);
                
                // Create translate matrix transform to shift coords to map points to new window location.
                MatrixTransform additionalTransform = new MatrixTransform(new Matrix(1, 0, 0, 1,
                                                            oldWindowLocation.X - newWindowLocation.X,
                                                            oldWindowLocation.Y - newWindowLocation.Y));
                _eventStylusPoints = _eventStylusPoints.Reformat(report.StylusPointDescription, additionalTransform);
            }

            _rawPosition = _eventStylusPoints[_eventStylusPoints.Count - 1];

            _inputSource = new SecurityCriticalDataClass<PresentationSource>(inputSource);

            if (inputSource != null)
            {
                // Update our screen position from this move.
                Point pt = _stylusLogic.DeviceUnitsFromMeasureUnits((Point)_rawPosition);
                _lastScreenLocation = PointUtil.ClientToScreen(pt, inputSource);
            }
            
            // If we are not blocked from updating the location we want to use for the 
            // promoted mouse location then update it.  We set this flag in the post process phase
            // of Stylus events (after they have fired).
            if (!_fBlockMouseMoveChanges)
            {
                _lastMouseScreenLocation = _lastScreenLocation;
            }

            if ((report.Actions & RawStylusActions.Down) != 0 ||
                 (report.Actions & RawStylusActions.Move) != 0)
            {
                _fInAir = false;

                // Keep the stylus down location for turning system gestures into mouse event
                if ((report.Actions & RawStylusActions.Down) != 0)
                {
                    _needToSendMouseDown = true;
                    // reset the gesture flag.  This is used to determine if we will need to fabricate a systemgesture tap on the 
                    // corresponding up event.
                    _fGestureWasFired = false;
                    _fDetectedDrag = false;
                    _seenHoldEnterGesture = false;

                    // Make sure our drag and move deltas are up to date.
                    TabletDevice.UpdateSizeDeltas(report.StylusPointDescription, _stylusLogic);
                }
                // See if we need to do our own Drag detection (on Stylus Move event)
                else if (inputSource != null && _fBlockMouseMoveChanges && _seenDoubleTapGesture && !_fGestureWasFired && !_fDetectedDrag)
                {
                    Size delta = TabletDevice.CancelSize;

                    // We use the first point of the packet data for Drag detection to try and
                    // filter out cases where the stylus skips when going down.
                    Point dragPosition =(Point)_eventStylusPoints[0];
                    dragPosition = _stylusLogic.DeviceUnitsFromMeasureUnits(dragPosition);
                    dragPosition = PointUtil.ClientToScreen(dragPosition, inputSource);

                    // See if we need to detect a Drag gesture.  If so do the calculation.
                    if ((Math.Abs(_lastMouseScreenLocation.X - dragPosition.X) > delta.Width) ||
                        (Math.Abs(_lastMouseScreenLocation.Y - dragPosition.Y) > delta.Height))
                    {
                        _fDetectedDrag = true;
                    }
                }
            }

            UpdateEventStylusPoints(report, false);

            if ((report.Actions & RawStylusActions.Up)        != 0 ||
                (report.Actions & RawStylusActions.InAirMove) != 0)
            {
                _fInAir = true;
                
                if ((report.Actions & RawStylusActions.Up) != 0)
                {
                    _sawMouseButton1Down = false; // reset this on Stylus Up.
                }
            }
        }


        ///////////////////////////////////////////////////////////////////////

        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical: Gets passed critical data (inputSource and ignoreSource).
        ///               Calls SecurityCritical methods (PresentationSource.CompositionTarget,
        ///                 PresentationSource.CriticalFromVisual, UnsafeNativeMethods.WindowFromPoint,
        ///                 HwndSource.CriticalFromHwnd).
        /// </SecurityNote>
        [SecurityCritical]
        private PresentationSource DetermineValidSource(PresentationSource inputSource, StylusPointCollection stylusPoints, PenContexts penContextsOfPoints)
        {
            HwndSource hwndSource = (HwndSource)inputSource;
            
            // See if window has been closed or is invalid
            if (inputSource.CompositionTarget == null || inputSource.CompositionTarget.IsDisposed ||
                hwndSource == null || hwndSource.IsHandleNull)
            {
                PresentationSource newSource = null;
                
                // use capture as fallback first
                if (_stylusCapture != null)
                {
                    DependencyObject containingVisual = InputElement.GetContainingVisual(_stylusCapture as DependencyObject);
                    PresentationSource capturedSource = PresentationSource.CriticalFromVisual(containingVisual);
                    
                    if (capturedSource != null && 
                        capturedSource.CompositionTarget != null &&
                        !capturedSource.CompositionTarget.IsDisposed)
                    {
                        newSource = capturedSource; // Good new source to use!
                    }
                }

                // Now try last screen point hittesting to find a new window/PresetationSource.
                if (newSource == null && stylusPoints != null)
                {
                    Point ptScreen;

                    // If we have the last penContext then we can remap the coordinates properly.
                    // Otherwise we just use the last stylus mouse location to figure out a PresenationSource.
                    if (penContextsOfPoints != null)
                    {
                        ptScreen = _stylusLogic.DeviceUnitsFromMeasureUnits((Point)stylusPoints[0]);
                        // map from window to screen (ie - add the window location).
                        ptScreen.Offset(penContextsOfPoints.DestroyedLocation.X, penContextsOfPoints.DestroyedLocation.Y);
                    }
                    else
                    {
                        ptScreen = _lastMouseScreenLocation;
                    }

                    IntPtr hwndHit = UnsafeNativeMethods.WindowFromPoint((int)ptScreen.X, (int)ptScreen.Y);
                    if (hwndHit != IntPtr.Zero)
                    {
                        HwndSource newHwndSource = HwndSource.CriticalFromHwnd(hwndHit);
                        if (newHwndSource != null && newHwndSource.Dispatcher == Dispatcher)
                        {
                            newSource = newHwndSource;
                        }
                    }
                }

                return newSource;
            }
            else
            {
                return inputSource;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical: Accesses security critical data _stylusLogic.
        ///                - Called from the StylusLogic::PreNotifyInput event handler
        /// </SecurityNote>
        [SecurityCritical]
        internal void UpdateInRange(bool inRange, PenContext penContext)
        {
            _fInRange = inRange;

            // Make sure we clean the last _inputSource for down at this time.
            //_inputSourceForDown = null;
            if (inRange)
                _activePenContext = new SecurityCriticalDataClass<PenContext>(penContext);
            else
                _activePenContext = null;
        }


        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical: Creates SecurityCriticalData (_inputSource) and accesses
        ///               SecurityCriticalData _stylusLogic.
        ///                - Called from the StylusLogic::PreNotifyInput event handler
        /// </SecurityNote>
        [SecurityCritical]
        internal void UpdateStateForSystemGesture(RawStylusSystemGestureInputReport report)
        {
            UpdateStateForSystemGesture(report.SystemGesture, report);
        }

        /// <SecurityNote>
        ///     Critical: Creates SecurityCriticalData (_inputSource) and accesses
        ///               SecurityCriticalData _stylusLogic.
        ///                - Called from the StylusLogic::PreNotifyInput event handler
        /// </SecurityNote>
        [SecurityCritical]
        private void UpdateStateForSystemGesture(SystemGesture gesture, RawStylusSystemGestureInputReport report)
        {
            switch (gesture)
            {
                case SystemGesture.Tap:
                case SystemGesture.Drag:
                    // request the next mouse move to become LeftButtonDown
                    _fLeftButtonDownTrigger = true;
                    _fGestureWasFired = true;
                    break;
                case SystemGesture.RightTap:
                case SystemGesture.RightDrag:
                    // request the next mouse move to become RightButtonDown
                    _fLeftButtonDownTrigger = false;
                    _fGestureWasFired = true;
                    break;
                case SystemGesture.HoldEnter:
                    // press & hold animation started..
                    _seenHoldEnterGesture = true;
                    break;
                case SystemGesture.Flick:
                    // We don't do any mouse promotion for a flick!
                    _fGestureWasFired = true;

                    // Update the stylus location info just for flick gestures.  This is because
                    // we want to fire the flick event not from the last stylus location
                    // (end of flick gesture) but from the beginning of the flick gesture
                    // (stylus down point) since this is the element that we query whether they
                    // allow flicks and since scrolling is targetted we need to scroll the
                    // element you really flicked on.

                    // Only route the flick if we have data we can send.
                    if (report != null && report.InputSource != null && _eventStylusPoints != null && _eventStylusPoints.Count > 0)
                    {
                        StylusPoint stylusPoint = _eventStylusPoints[_eventStylusPoints.Count - 1];

                        stylusPoint.X = report.GestureX;
                        stylusPoint.Y = report.GestureY;

                        // Update the current point with this data.
                        _eventStylusPoints = new StylusPointCollection(stylusPoint.Description,
                                                                       stylusPoint.GetPacketData(),
                                                                       GetTabletToElementTransform(null),
                                                                       Matrix.Identity);

                        PresentationSource inputSource = DetermineValidSource(report.InputSource, _eventStylusPoints, report.PenContext.Contexts);

                        if (inputSource != null)
                        {
                            // See if we need to remap the stylus data X and Y values to different presentation source.
                            if (inputSource != report.InputSource)
                            {
                                Point newWindowLocation = PointUtil.ClientToScreen(new Point(0, 0), inputSource);
                                newWindowLocation = _stylusLogic.MeasureUnitsFromDeviceUnits(newWindowLocation);
                                Point oldWindowLocation = _stylusLogic.MeasureUnitsFromDeviceUnits(report.PenContext.Contexts.DestroyedLocation);

                                // Create translate matrix transform to shift coords to map points to new window location.
                                MatrixTransform additionalTransform = new MatrixTransform(new Matrix(1, 0, 0, 1,
                                                                            oldWindowLocation.X - newWindowLocation.X,
                                                                            oldWindowLocation.Y - newWindowLocation.Y));
                                _eventStylusPoints = _eventStylusPoints.Reformat(report.StylusPointDescription, additionalTransform);
                            }

                            _rawPosition = _eventStylusPoints[_eventStylusPoints.Count - 1];
                            _inputSource = new SecurityCriticalDataClass<PresentationSource>(inputSource);
                            Point pt = _stylusLogic.DeviceUnitsFromMeasureUnits((Point)_rawPosition);
                            _lastScreenLocation = PointUtil.ClientToScreen(pt, inputSource);
                        }
                    }

                    break;
            }
        }


        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical - Calls in to SecurityCritical code (StylusLogic::InputManagerProcesssInput)
        ///                and accesses SecurityCriticalData _stylusLogic.
        ///      At the top called from StylusLogic::PostProcessInput event which is SecurityCritical
        /// </SecurityNote>
        [SecurityCritical]
        internal void PlayBackCachedDownInputReport(int timestamp)
        {
            if (_needToSendMouseDown)
            {
                // if we have marked this as handled we need to play the down otherwise we can ignore the down
                // as it will be process anyway and either way we need to clean up the cached down
                PresentationSource mouseInputSource = GetMousePresentationSource();

                if (mouseInputSource != null)
                {
                    Point pt = PointUtil.ScreenToClient(_lastMouseScreenLocation, mouseInputSource);

                    _needToSendMouseDown = false; // We've sent down, don't send again.

                    // Update the state we report to the mouse (GetButtonState).
                    _promotedMouseState = MouseButtonState.Pressed;

                    RawMouseActions actions = _fLeftButtonDownTrigger?RawMouseActions.Button1Press:RawMouseActions.Button2Press;

                    // StylusLogic manages the mouse state reported to the MouseDevice to deal with multiple stylusdevice input.
                    if (_stylusLogic.UpdateMouseButtonState(actions))
                    {
                        // See if we need to set the Mouse Activate flag.
                        InputManager inputManager = (InputManager)Dispatcher.InputManager;

                        if (inputManager != null)
                        {
                            if (inputManager.PrimaryMouseDevice.CriticalActiveSource != mouseInputSource)
                            {
                                actions |= RawMouseActions.Activate;
                            }               
                        }

                        // Update the last event we've sent through.
                        _stylusLogic.SetLastRawMouseActions(actions);
                        
                        RawMouseInputReport mouseInputReport = new RawMouseInputReport(
                                                     InputMode.Foreground, timestamp, mouseInputSource,
                                                     actions, 
                                                     (int)pt.X, (int)pt.Y, 0, IntPtr.Zero);

                        InputReportEventArgs inputReportArgs = new InputReportEventArgs(this, mouseInputReport);
                        inputReportArgs.RoutedEvent=InputManager.PreviewInputReportEvent;
                        _stylusLogic.InputManagerProcessInputEventArgs(inputReportArgs);
                    }
                }
                    
                _needToSendMouseDown = false; // so we don't try and resend it later.
                
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <SecurityNote>
        ///     Critical: - Accesses and hands out critical data. (HwndSource)
        ///               - Calls SecurityCritical code PresentationSource.CriticalFromVisual.
        ///      At the top called from StylusLogic::PostProcessInput event which is SecurityCritical
        /// </SecurityNote>
        [SecurityCritical]
        internal PresentationSource GetMousePresentationSource()
        {
            // See if we need to adjust the mouse point to a different
            // presentation source.  We have to do this if the mouse has capture.
            InputManager inputManager = (InputManager)Dispatcher.InputManager;
            PresentationSource mouseInputSource = null;
            if (inputManager != null)
            {
                IInputElement mouseCaptured = inputManager.PrimaryMouseDevice.Captured;
                if (mouseCaptured != null)
                {
                    // See if mouse is captured to a different window (HwndSource will be different)
                    // NOTE: Today we can only translate points between HwndSources (PresentationSource doesn't support this)
                    DependencyObject mouseCapturedVisual = InputElement.GetContainingVisual((DependencyObject)mouseCaptured);
                    if (mouseCapturedVisual != null)
                    {
                        mouseInputSource = PresentationSource.CriticalFromVisual(mouseCapturedVisual);
                    }
                }
                else if (_stylusOver != null)
                {
                    // Use our current input source (or one we're may be over) if no capture.
                    mouseInputSource = (_inputSource != null && _inputSource.Value != null) ? 
                                            DetermineValidSource(_inputSource.Value, _eventStylusPoints, null) : null;
                }
            }

            return mouseInputSource;
        }

        /// <SecurityNote>
        ///      Critical as this calls a critical method. (PlayBackCachedDownInputReport)
        ///         and access critical member _inputReportCachedMoves
        ///
        ///      At the top called from StylusLogic::PostProcessInput event which is SecurityCritical
        /// </SecurityNote>
        [SecurityCritical]
        internal RawMouseActions GetMouseActionsFromStylusEventAndPlaybackCachedDown(RoutedEvent stylusEvent, StylusEventArgs stylusArgs)
        {
            if (stylusEvent == Stylus.StylusSystemGestureEvent)
            {
                // See if this is an OK gesture to trigger a mouse event on.
                StylusSystemGestureEventArgs systemGestureArgs = (StylusSystemGestureEventArgs)stylusArgs;
                if (systemGestureArgs.SystemGesture == SystemGesture.Tap ||
                    systemGestureArgs.SystemGesture == SystemGesture.RightTap ||
                    systemGestureArgs.SystemGesture == SystemGesture.Drag ||
                    systemGestureArgs.SystemGesture == SystemGesture.RightDrag ||
                    systemGestureArgs.SystemGesture == SystemGesture.Flick)
                {
                    // Usually UpdateStateForSystemGesture happens in the PreNotify.
                    // And UpdateState for other stylus events happens during mouse promotion.
                    // But with manipulations when events are stored for future promotion,
                    // this difference in order could cause problems. Hence reexecute
                    // UpdateStateForSystemGesture to fix the order.
                    UpdateStateForSystemGesture(systemGestureArgs.SystemGesture, null);

                    if (systemGestureArgs.SystemGesture == SystemGesture.Drag ||
                        systemGestureArgs.SystemGesture == SystemGesture.RightDrag ||
                        systemGestureArgs.SystemGesture == SystemGesture.Flick)
                    {
                        _fBlockMouseMoveChanges = false;
                        TapCount = 1; // reset on a drag or flick.
                        if (systemGestureArgs.SystemGesture == SystemGesture.Flick)
                        {
                            // Don't want to play down or cached moves.
                            _needToSendMouseDown = false;
                        }
                        else
                        {
                            PlayBackCachedDownInputReport(systemGestureArgs.Timestamp);
                        }
                    }
                    else //we have a Tap
                    {
                        PlayBackCachedDownInputReport(systemGestureArgs.Timestamp);
                    }
                }
            }
            else if (stylusEvent == Stylus.StylusInAirMoveEvent)
            {
                return RawMouseActions.AbsoluteMove;
            }
            else if (stylusEvent == Stylus.StylusDownEvent)
            {
                _fLeftButtonDownTrigger = true; // Default to left click until system gesture says otherwise.
                _fBlockMouseMoveChanges = true;
                
                // See if we can promote the mouse button down right now.
                if (_seenDoubleTapGesture || _sawMouseButton1Down)
                {
                    PlayBackCachedDownInputReport(stylusArgs.Timestamp);
                }
            }
            else if (stylusEvent == Stylus.StylusMoveEvent)
            {
                if (!_fBlockMouseMoveChanges)
                {
                    return RawMouseActions.AbsoluteMove;
                }
            }
            else if (stylusEvent == Stylus.StylusUpEvent)
            {
                _fBlockMouseMoveChanges = false;
                _seenDoubleTapGesture = false; // reset this on Stylus Up.
                _sawMouseButton1Down = false; // reset to make sure we don't promote a mouse down on the next stylus down.
                
                if (_promotedMouseState == MouseButtonState.Pressed)
                {
                    _promotedMouseState = MouseButtonState.Released;
                    RawMouseActions actions = _fLeftButtonDownTrigger ? 
                                                    RawMouseActions.Button1Release : 
                                                    RawMouseActions.Button2Release;
                    // Make sure we only promote a mouse up if the mouse is in the down 
                    // state (UpdateMousebuttonState returns true in that case)!
                    if (_stylusLogic.UpdateMouseButtonState(actions))
                    {
                        return actions;
                    }
                    // else - just return default of RawMouseActions.None since we don't want this 
                    //        duplicate mouse up to be processed.
                }
            }

            // Default return
            return RawMouseActions.None;
        }


        /////////////////////////////////////////////////////////////////////


        internal Point LastMouseScreenPoint
        {
            get { return _lastMouseScreenLocation; }
            set { _lastMouseScreenLocation = value; }
        }

        internal bool SeenDoubleTapGesture
        {
            get { return _seenDoubleTapGesture; }
            set { _seenDoubleTapGesture = value; }
        }

        internal bool SeenHoldEnterGesture
        {
            get { return _seenHoldEnterGesture; }
        }

        internal bool GestureWasFired
        {
            get { return _fGestureWasFired;}
        }

        internal bool SentMouseDown
        {
            get { return _promotedMouseState == MouseButtonState.Pressed;}
        }

        internal bool DetectedDrag
        {
            get { return _fDetectedDrag; }
        }

        internal bool LeftIsActiveMouseButton
        {
            get { return _fLeftButtonDownTrigger; }
        }

        internal void SetSawMouseButton1Down(bool sawMouseButton1Down)
        {
            _sawMouseButton1Down = sawMouseButton1Down;
        }

        internal bool IgnoreStroke
        {
            get { return _ignoreStroke; }
            set { _ignoreStroke = value; }
        }

        #region Touch

        internal StylusTouchDevice TouchDevice
        {
            get
            {
                if (_touchDevice == null)
                {
                    _touchDevice = new StylusTouchDevice(this);
                }

                return _touchDevice;
            }
        }

        /// <SecurityNote>
        ///     Critical - Accesses CriticalActiveSource.
        ///     TreatAsSafe - Updates a TouchDevice that it owns.
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        internal void UpdateTouchActiveSource()
        {
            if (_touchDevice != null)
            {
                PresentationSource activeSource = CriticalActiveSource;
                if (activeSource != null)
                {
                    _touchDevice.ChangeActiveSource(activeSource);
                }
            }
        }

        #endregion

#if MULTICAPTURE
        private DeferredElementTreeState StylusOverTreeState
        {
            get
            {
                if (_stylusOverTreeState == null)
                {
                    _stylusOverTreeState = new DeferredElementTreeState();
                }

                return _stylusOverTreeState;
            }
        }

        private DeferredElementTreeState StylusCaptureWithinTreeState
        {
            get
            {
                if (_stylusCaptureWithinTreeState == null)
                {
                    _stylusCaptureWithinTreeState = new DeferredElementTreeState();
                }

                return _stylusCaptureWithinTreeState;
            }
        }
#endif

        /////////////////////////////////////////////////////////////////////

        TabletDevice            _tabletDevice;
        string                  _sName;
        int                     _id;
        bool                    _fInverted;
        bool                    _fInRange;
        StylusButtonCollection  _stylusButtonCollection;
        IInputElement           _stylusOver;
#if MULTICAPTURE
        private DeferredElementTreeState _stylusOverTreeState;
#endif
        
        IInputElement           _stylusCapture;
        CaptureMode             _captureMode;
#if MULTICAPTURE
        private DeferredElementTreeState _stylusCaptureWithinTreeState;
#endif
        StylusPoint             _rawPosition = new StylusPoint(0, 0);
        Point                   _rawElementRelativePosition = new Point(0, 0);
        StylusPointCollection   _eventStylusPoints;

        /// <SecurityNote>
        ///     This data is not safe to expose as it holds refrence to PresentationSource
        /// </SecurityNote>
        private SecurityCriticalDataClass<PresentationSource> _inputSource;

        /// <SecurityNote>
        ///     This data is not safe to expose as it holds refrence to PenContext
        /// </SecurityNote>
        private SecurityCriticalDataClass<PenContext> _activePenContext;
 
        bool          _needToSendMouseDown;
        private Point _lastMouseScreenLocation = new Point(0,0);
        private Point _lastScreenLocation = new Point(0,0);

        bool                    _fInAir = true;
        bool                    _fLeftButtonDownTrigger = true; // default to left button down
        bool                    _fGestureWasFired = true; // StylusDown resets this.
        bool                    _fBlockMouseMoveChanges; // StylusDown sets to true, SystemGesture & StylusUp sets to false.
        bool                    _fDetectedDrag; // StylusDown resets this.  Used for generating DoubleTap gestures.

        // Used to track the promoted mouse state.
        MouseButtonState        _promotedMouseState;

        // real time pen input info that is tracked per stylus device
        StylusPlugInCollection  _nonVerifiedTarget;
        StylusPlugInCollection  _verifiedTarget;

        object _rtiCaptureChanged = new object();
        StylusPlugInCollection _stylusCapturePlugInCollection;


        // Information used to distinguish double-clicks (actually, multi clicks) from
        // multiple independent clicks.
        private Point _lastTapXY = new Point(0,0);
        private int _tapCount;
        private int _lastTapTime;
        private bool _lastTapBarrelDown;

        private bool _seenDoubleTapGesture;
        private bool _seenHoldEnterGesture;

        private bool _sawMouseButton1Down; // Did we see the mouse down before the stylus down?
        private bool _ignoreStroke; // Should we ignore promoting the stylus/mouse events for the current stroke?

        /// <SecurityNote>
        ///     Critical to prevent accidental spread to transparent code
        /// </SecurityNote>
        [SecurityCritical]
        private StylusLogic _stylusLogic;

        private StylusTouchDevice _touchDevice;

#if MULTICAPTURE
        private DependencyPropertyChangedEventHandler _overIsEnabledChangedEventHandler;
        private DependencyPropertyChangedEventHandler _overIsVisibleChangedEventHandler;
        private DependencyPropertyChangedEventHandler _overIsHitTestVisibleChangedEventHandler;
        private DispatcherOperationCallback _reevaluateStylusOverDelegate;
        private DispatcherOperation _reevaluateStylusOverOperation;

        private DependencyPropertyChangedEventHandler _captureIsEnabledChangedEventHandler;
        private DependencyPropertyChangedEventHandler _captureIsVisibleChangedEventHandler;
        private DependencyPropertyChangedEventHandler _captureIsHitTestVisibleChangedEventHandler;
        private DispatcherOperationCallback _reevaluateCaptureDelegate;
        private DispatcherOperation _reevaluateCaptureOperation;
#endif
    }
}
