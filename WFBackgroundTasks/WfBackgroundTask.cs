﻿// #define PRINT_TICKET_TEST 
// Copyright(c) Microsoft Corporation.All rights reserved.
//
//   THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
//   ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO
//   THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
//   PARTICULAR PURPOSE.
//
//   This is a sample implementation of the Print Workflow application. The background task Run() is called when something is printed to
//   a printer that has been associated with a Workflow application. The Run() method is used to determine whether UI is required, and 
//   to add the callbacks which will be invoked when sessionManager.Start() is called. The background task uses the XPS Object Model
//   Windows Runtime component to create a IPrintWorkflowXpsReceiver implementation, called on a different thread to the App. The
//   generator receives the document object model parts, and we use the XPS Object Model to add watermark text and a logo image
//   before being sent on to the printer.
//
//   The sample demonstrates the background callbacks which are, in the order called, OnSetupRequested() and OnXpsOMPrintSubmitted()
//   which are set by the the sessionManager.SetupRequested and sessionManager.Submitted properties.
//   The UI foreground task cannot change the XPS data, and this must be done by the background task. Hence any parameters gathered
//   by the UI are saved in local storage for use by the background task, which actually makes the changes.
//
//   This sample also demonstrates how to ensure that the application does not suspend while still processing background tasks.
//   The main application code in App.xaml.cs will call the public static method in this file to ensure that all background tasks
//   have exited during the Suspending handler, which will give up to 30 minutes to complete processing.
//
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Foundation;
using Windows.Graphics.Printing.PrintTicket;
using Windows.Graphics.Printing.Workflow;
using Windows.UI.Notifications;
using WorkflowAndWSDACombinedSample;
using XpsOMRuntimeComponent;

namespace WFBackgroundTasks
{
    public sealed class WfBackgroundTask : IBackgroundTask
    {
        #region Background task ref-counting
        /// <summary>
        /// These private static variables track the number of active workflow background tasks.
        /// The integer is initialized to zero, but the first task's Run method will increment it and reset the
        /// event.  When the count drops back down to zero, the event will be signaled that it's safe to allow the app to suspend.
        /// </summary>
        static int TaskRefCount = 0;
        static System.Threading.ManualResetEvent AllTasksDoneEvent = new System.Threading.ManualResetEvent(true);

        /// <summary>
        /// This method exposes a way for the main application code to wait until all of the background tasks exit.
        /// </summary>
        public static void WaitForTasks()
        {
            AllTasksDoneEvent.WaitOne();
        }
        #endregion

        #region Public member functions
        /// <summary>
        /// The background task is first invoked here in the standard Run method
        /// </summary>
        /// <param name="taskInstance">This contains the TriggerDetails object, which is actually Windows.Graphics.Printing.Workflow.WorkflowTriggerDetails</param>
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // Take out a deferral here and complete once all the callbacks are done
            runDeferral = taskInstance.GetDeferral();

            if (1 == System.Threading.Interlocked.Increment(ref TaskRefCount))
            {
                AllTasksDoneEvent.Reset();
            }

            // Associate a cancellation handler with the background task.
            taskInstance.Canceled += new BackgroundTaskCanceledEventHandler(OnCanceled);

            try
            {
                PrintWorkflowTriggerDetails workflowTriggerDetails = taskInstance.TriggerDetails as PrintWorkflowTriggerDetails;

                // Get the activation arguments
                PrintWorkflowBackgroundSession sessionManager = workflowTriggerDetails.PrintWorkflowSession;

                // The workflow details object has the workflow session manager,
                //  which is unique to this print job and use it to add the event handler callback routines
                sessionManager.SetupRequested += OnSetupRequested;

                // XPS OM printing scenario
                // sessionManager.Submitted += OnXpsOMPrintSubmitted;
                sessionManager.Submitted += OnXpsOMPrintSubmittedAsync;

                // Tell the event source that it can start
                // This call blocks until all the workflow callbacks complete
                sessionManager.Start();
            }
            finally
            {
                // At this point, all of the callbacks have completed, so it's safe
                // to decrement the workflow task ref count and possibly signal the
                // application that it's safe to suspend.
                if (0 == System.Threading.Interlocked.Decrement(ref TaskRefCount))
                {
                    AllTasksDoneEvent.Set();
                }
            }
        }
        #endregion

        #region Private member functions
        /// <summary>
        /// Handle the Print Task Setup Event
        /// Raised when the source app sets the Print Ticket. This event provides a callback method for the app to signal it is done handling this event
        /// and optionally specifying the need for UI, which will cause the UI part of this App to be launched in OnActivated() with ActivationKind.PrintWorkflowForegroundTask .
        /// </summary>
        /// <param name="sessionManager">Session manager</param>
        /// <param name="printTaskSetupArgs">Has the Configuration, which include the PrintTicket, and other information</param>
        private void OnSetupRequested(PrintWorkflowBackgroundSession sessionManager, PrintWorkflowBackgroundSetupRequestedEventArgs printTaskSetupArgs)
        {
            System.Diagnostics.Debug.WriteLine("Sample::WfBackgroundTask::OnSetupRequested-Enters");
            // Request a deferral if any of the calls here might result in an Async method being called 
            Deferral setupRequestedDeferral = printTaskSetupArgs.GetDeferral();

            // Get information about the source application, print job title, and session ID
            string sourceApplicationName = printTaskSetupArgs.Configuration.SourceAppDisplayName;
            string jobTitle = printTaskSetupArgs.Configuration.JobTitle;
            string sessionId = printTaskSetupArgs.Configuration.SessionId;

            // Check to see if user wanted to use the watermark already when the program was run "standalone" and saved in local storage
            // and not have UI every time. It can always be reset by running the app again and unchecking the check box
            localStorage = new LocalStorageUtilities();
            suppressUI = localStorage.GetUseStandaloneSettings();
            if (!suppressUI)
            {
                // Indicate that we need to launch the UI, in this example, because we need to get watermarking text, and optionally an image
                printTaskSetupArgs.SetRequiresUI();
                // Set storage prefix so that foreground and background can pass properties
                string localStorageVariablePrefix = string.Format("{0}::", sessionId.Substring(0, 8));
                localStorage.SetStorageKeyPrefix(localStorageVariablePrefix);

            }
            else
            {
                // Use unprefixed default values set in "standalone" mode, without showing UI when activated in workflow mode
                localStorage.SetStorageKeyPrefix("");
            }

            // Complete the deferral taken out at the start of OnSetupRequested
            setupRequestedDeferral.Complete();
        }

        /// <summary>
        /// Handle the Print Task Submitted Event using the XPS Object Model
        /// Raised after the UI part of the app has, optionally, had its OnSetupRequested and OnXpsDataAvailable called.
        /// This event provides all data already provided on the OnSetupRequested event. It additionally provides
        /// a reference to the PrintWorkflowObjectModelTargetPackage interface enabling manipulation of the output via the XPS object model.
        /// </summary>
        /// <param name="sessionManager">Session manager</param>
        /// <param name="printTaskSubmittedArgs">Has the Controller, Content, and Configuration</param>
        private void OnXpsOMPrintSubmitted(PrintWorkflowBackgroundSession sessionManager, PrintWorkflowSubmittedEventArgs printTaskSubmittedArgs)
        {
            // Take out a deferral whilst the OM generation happens
            Deferral submittedDeferral = printTaskSubmittedArgs.GetDeferral();

            // Send a toast
            UtilitiesLibrary.SendToastNotification("Background Print Workflow printing started for " + printTaskSubmittedArgs.Operation.Configuration.JobTitle, null);

            // Get the source XPS OM content
            PrintWorkflowObjectModelSourceFileContent xpsOMSourceContent = printTaskSubmittedArgs.Operation.XpsContent.GetSourceSpoolDataAsXpsObjectModel();

            // Send the print ticket if available to get the Target
            PrintWorkflowTarget target = printTaskSubmittedArgs.GetTarget(null);

            // Get the Target Package
            PrintWorkflowObjectModelTargetPackage targetPackage = target.TargetAsXpsObjectModelPackage;

            // Create a Windows Runtime XPS Receiver callback object via the XpsOMRuntimeComponent, which implements
            // the IPrintWorkflowXpsReceiver interface and PrintWorkflowObjectModelSourceFileContentNative. It is in the
            // IPrintWorkflowXpsReceiver implementation that the output is actually modified
            var xpsReceiver = new PrintWorkflowObjectModelSourceFileContentNative(xpsOMSourceContent, targetPackage);

            // Signal for the XPS OM Generation
            PrintWorkflowSubmittedStatus submittedStatus = PrintWorkflowSubmittedStatus.Failed;
            try
            {
                // Get the watermark text and pass that to the xpsReceiver
                string watermarkText = localStorage.GetWatermarkTextFromLocalStorage();
                if (!suppressUI)
                {
                    // Delete the local storage setting if this is in a Workflow session context and not a standalone run of the app
                    localStorage.DeleteWatermarkTextFromLocalStorage();
                }

                xpsReceiver.SetWatermarkText(watermarkText);

                // Get the image file information, if set, and pass that to the xpsReceiver
                localStorage.GetImagePropertiesFromLocalStorage(out string imageFile, out double dpiX, out double dpiY, out int imageWidth, out int imageHeight);
                if (imageFile != null)
                {
                    xpsReceiver.SetImageProperties(imageFile, dpiX, dpiY, imageWidth, imageHeight);
                    if (!suppressUI)
                    {
                        localStorage.DeleteImagePropertiesFromLocalStorage();
                    }
                }

                // Start the OM generation
                xpsReceiver.StartXpsOMGeneration();
                // At this point, the xpsReceiver will receive callbacks when the XPS OM objects are available
                // Wait till the Whole XPS OM is generated and written to the package writer
                if (xpsReceiver.WaitForOMGeneration())
                {
                    UtilitiesLibrary.SendToastNotification("Background Print Workflow printing successfully completed for " + printTaskSubmittedArgs.Operation.Configuration.JobTitle, null);
                    submittedStatus = PrintWorkflowSubmittedStatus.Succeeded;
                }
                else
                {
                    UtilitiesLibrary.SendToastNotification("Background Print Workflow printing failed for " + printTaskSubmittedArgs.Operation.Configuration.JobTitle, null);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = ex.Message;
                UtilitiesLibrary.SendToastNotification("Background Print Workflow printing encountered an exception: " + errorMessage, null);
            }
            finally
            {
                // All done, let the controller whether it should close the job stream or abort it
                printTaskSubmittedArgs.Operation.Complete(submittedStatus);

                // Complete the deferral taken out at the start of this function
                submittedDeferral.Complete();

                // Complete the deferral taken out at the start of Run()
                runDeferral.Complete();
            }
        }

        private async void OnXpsOMPrintSubmittedAsync(PrintWorkflowBackgroundSession sessionManager, PrintWorkflowSubmittedEventArgs printTaskSubmittedArgs)
        {
            System.Diagnostics.Debug.WriteLine("Sample::WfBackgroundTask::OnXpsOMPrintSubmittedAsync-Enters");
            // Take out a deferral whilst the OM generation happens
            Deferral submittedDeferral = printTaskSubmittedArgs.GetDeferral();

            // Send a toast
            UtilitiesLibrary.SendToastNotification("Background Print Workflow printing started for " + printTaskSubmittedArgs.Operation.Configuration.JobTitle, null);

            // Get the source XPS OM content
            PrintWorkflowObjectModelSourceFileContent xpsOMSourceContent = printTaskSubmittedArgs.Operation.XpsContent.GetSourceSpoolDataAsXpsObjectModel();

            WorkflowPrintTicket ticket = null;
#if PRINT_TICKET_TEST
            // Both cases - Print ticket is not changed from WSDA context
            // Send the print ticket if available to get the Target
            if ( 
                printTaskSubmittedArgs != null 
                && printTaskSubmittedArgs.Operation != null 
                && printTaskSubmittedArgs.Operation.XpsContent != null)
            { 
                ticket = await printTaskSubmittedArgs.Operation.XpsContent.GetJobPrintTicketAsync();
            
                if (ticket != null && ticket.XmlNode != null)
                {
                    System.Diagnostics.Debug.WriteLine("Sample::WfBackgroundTask::OnXpsOMPrintSubmitted:: args.Operation.XpsContent.GetJobPrintTicketAsync Content: \n" + ticket.XmlNode.GetXml());
                }
            }
#endif
            PrintWorkflowTarget target = printTaskSubmittedArgs.GetTarget(ticket);
            if( ticket == null )
            {
                if (
                printTaskSubmittedArgs != null
                && printTaskSubmittedArgs.Operation != null
                && printTaskSubmittedArgs.Operation.XpsContent != null)
                {
                    ticket = await printTaskSubmittedArgs.Operation.XpsContent.GetJobPrintTicketAsync();

                    if (ticket != null && ticket.XmlNode != null)
                    {
                        System.Diagnostics.Debug.WriteLine("Args.Operation.XpsContent.GetJobPrintTicketAsync Content: \n" + ticket.XmlNode.GetXml());
                    }
                }
            }
            // Get the Target Package
            PrintWorkflowObjectModelTargetPackage targetPackage = target.TargetAsXpsObjectModelPackage;

            // Create a Windows Runtime XPS Receiver callback object via the XpsOMRuntimeComponent, which implements
            // the IPrintWorkflowXpsReceiver interface and PrintWorkflowObjectModelSourceFileContentNative. It is in the
            // IPrintWorkflowXpsReceiver implementation that the output is actually modified
            var xpsReceiver = new PrintWorkflowObjectModelSourceFileContentNative(xpsOMSourceContent, targetPackage);

            // Signal for the XPS OM Generation
            PrintWorkflowSubmittedStatus submittedStatus = PrintWorkflowSubmittedStatus.Failed;
            try
            {
                System.Diagnostics.Debug.WriteLine("Sample::WfBackgroundTask::OnXpsOMPrintSubmitted- GetJobPrintTicketAsync");
                
                // Get the watermark text and pass that to the xpsReceiver
                string watermarkText = localStorage.GetWatermarkTextFromLocalStorage();
                if (!suppressUI)
                {
                    // Delete the local storage setting if this is in a Workflow session context and not a standalone run of the app
                    localStorage.DeleteWatermarkTextFromLocalStorage();
                }

                xpsReceiver.SetWatermarkText(watermarkText);

                // Get the image file information, if set, and pass that to the xpsReceiver
                localStorage.GetImagePropertiesFromLocalStorage(out string imageFile, out double dpiX, out double dpiY, out int imageWidth, out int imageHeight);
                if (imageFile != null)
                {
                    xpsReceiver.SetImageProperties(imageFile, dpiX, dpiY, imageWidth, imageHeight);
                    if (!suppressUI)
                    {
                        localStorage.DeleteImagePropertiesFromLocalStorage();
                    }
                }

                // Start the OM generation
                xpsReceiver.StartXpsOMGeneration();
                // At this point, the xpsReceiver will receive callbacks when the XPS OM objects are available
                // Wait till the Whole XPS OM is generated and written to the package writer
                if (xpsReceiver.WaitForOMGeneration())
                {
                    UtilitiesLibrary.SendToastNotification("Background Print Workflow printing successfully completed for " + printTaskSubmittedArgs.Operation.Configuration.JobTitle, null);
                    submittedStatus = PrintWorkflowSubmittedStatus.Succeeded;
                }
                else
                {
                    UtilitiesLibrary.SendToastNotification("Background Print Workflow printing failed for " + printTaskSubmittedArgs.Operation.Configuration.JobTitle, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Sample::WfBackgroundTask::OnXpsOMPrintSubmitted-catch");
                string errorMessage = ex.Message;
                UtilitiesLibrary.SendToastNotification("Background Print Workflow printing encountered an exception: " + errorMessage, null);
                System.Diagnostics.Debug.WriteLine(
                       "Sample::WfBackgroundTask::OnXpsOMPrintSubmitted-Exception: " + errorMessage);

            }
            finally
            {
                System.Diagnostics.Debug.WriteLine("Sample::WfBackgroundTask::OnXpsOMPrintSubmitted-Finally Block");
                // All done, let the controller whether it should close the job stream or abort it
                printTaskSubmittedArgs.Operation.Complete(submittedStatus);

                // Complete the deferral taken out at the start of this function
                submittedDeferral.Complete();

                // Complete the deferral taken out at the start of Run()
                runDeferral.Complete();
            }
            System.Diagnostics.Debug.WriteLine("Sample::WfBackgroundTask::OnXpsOMPrintSubmittedAsync-Leaves");
        }

        /// <summary>
        /// Standard Cancel handler for a background task
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="reason"></param>
        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            //
            // Indicates that the background task is canceled.
            //
            UtilitiesLibrary.SendToastNotification("Background Print Workflow Task " + sender.Task.Name + " received a Cancel Request, reason " + reason.ToString() + ", instance " + sender.InstanceId.ToString(), null);
            Task.Delay(60000);
        }
        #endregion

        #region Private member variables
        // Private member variables
        private const string keywordNamespace = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";
        private LocalStorageUtilities localStorage;
        private BackgroundTaskDeferral runDeferral;
        private bool suppressUI = false;
        #endregion
    }
}
