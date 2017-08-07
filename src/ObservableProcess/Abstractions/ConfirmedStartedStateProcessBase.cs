﻿using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Elastic.ProcessManagement.Std;

namespace Elastic.ProcessManagement.Abstractions
{
	/// <summary>
	/// An abstraction that allows you to wrap an <see cref="IObservableProcess{TConsoleOut}"/>
	/// and block untill the wrapped process reports on console out its really started and ready.
	/// It also allows you to optionally give up the subscription after you've signalled the process has started
	/// </summary>
	public abstract class ConfirmedStartedStateProcessBase<TProcess> : IDisposable
		where TProcess : class, IObservableProcess<CharactersOut>, ISubscribeLines
	{
		public int? LastExitCode => this._process?.ExitCode;
		private readonly object _lock = new object();
		private readonly ManualResetEvent _completedHandle = new ManualResetEvent(false);
		private readonly ManualResetEvent _startedHandle = new ManualResetEvent(false);
		private readonly IConsoleOutWriter _writer;
		private readonly TProcess _process;

		private bool _started;
		private CompositeDisposable _disposables = new CompositeDisposable();

		protected ConfirmedStartedStateProcessBase(
			TProcess observableProcess,
			IConsoleOutWriter consoleOutWriter
			)
		{
			this._process = observableProcess ?? throw new ArgumentNullException(nameof(observableProcess));
			this._writer = consoleOutWriter ?? new ConsoleOutWriter();
		}

		/// <summary>
		/// A human readable string that can be used to identify the process in exception messages
		/// </summary>
		protected abstract string PrintableName { get; }

		/// <summary>
		/// Handle the messages coming out of your process, return true to signal the process was confirmed to be in started state.
		/// </summary>
		/// <returns>Return true to signal you've confirmed the process has really started</returns>
		protected abstract bool HandleMessage(LineOut message);

		/// <summary>
		/// Start the observable process and monitor its std out and error for a functional started message
		/// </summary>
		/// <param name="waitTimeout">How long we want to wait for the started confirmation before bailing</param>
		/// <param name="subscribeToMessagesAfterStartedConfirmation">
		/// By default we no longer subscribe after we recieve the started confirmation. Set this to true to
		/// keep receiving the console out messages wrapped in <see cref="ConsoleOut"/>. This boolean has no effect on the running state.
		/// The process will keep running until it either decides to stop or <see cref="Stop"/> is called.
		/// </param>
		/// <exception cref="CleanExitException">an exception that indicates a problem early in the pipeline</exception>
		public virtual void Start(TimeSpan waitTimeout = default(TimeSpan), bool subscribeToMessagesAfterStartedConfirmation = false)
		{
			var timeout = waitTimeout == default(TimeSpan) ? TimeSpan.FromMinutes(2) : waitTimeout;
			lock (_lock)
			{
				this._startedHandle.Reset();
				this._completedHandle.Reset();

				var observable = this._process.Publish();
				if (subscribeToMessagesAfterStartedConfirmation)
				{
					//subscribe to all messages and write them to console
					this._disposables.Add(observable.Subscribe(this._writer.Write, this._writer.Write, delegate { }));
				}

				//subscribe as long we are not in started state and attempt to read console
				//out for this confirmation
				this._disposables.Add(observable
					.TakeWhile(c => !this._started)
					.Select(LineOut.From)
					.Subscribe(this.Handle, delegate { }, delegate { })
				);
				this._disposables.Add(observable.Subscribe(delegate { }, HandleException, HandleCompleted));
				this._disposables.Add(observable.Connect());

				if (this._startedHandle.WaitOne(timeout)) return;
			}
			this.Stop();
			throw new CleanExitException($"Could not start process within ({timeout}): {PrintableName}");
		}

		/// <summary>
		/// Block until the process completes.
		/// </summary>
		/// <param name="timeout">The maximum time span we are willing to wait</param>
		/// <exception cref="CleanExitException">an exception that indicates a problem early in the pipeline</exception>
		public void WaitForCompletion(TimeSpan timeout)
		{
			lock (_lock)
			{
				if (!this._completedHandle.WaitOne(timeout))
					throw new CleanExitException($"Could not run process to completion within ({timeout}): {PrintableName}");
			}
		}

		private void Stop()
		{
			lock (_lock)
			{
				this._completedHandle.Reset();
				this._startedHandle.Reset();
				this.OnBeforeStop();
				this._process?.Dispose();
				this._disposables?.Dispose();
				this._disposables = new CompositeDisposable();
			}
		}

		protected virtual void OnBeforeStop()
		{

		}


		private void ConfirmProcessStarted()
		{
			this._started = true;
			this._startedHandle.Set();
		}

		private void HandleException(Exception e)
		{
			this._completedHandle.Set();
			this._startedHandle.Set();
			if (e is CleanExitException) return;
			throw e;
		}

		private void HandleCompleted()
		{
			this._startedHandle.Set();
			this._completedHandle.Set();
		}

		private void Handle(LineOut message)
		{
			if (this.HandleMessage(message))
				this.ConfirmProcessStarted();
		}

		public void Dispose() => this.Stop();
	}
}
