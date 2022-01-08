﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Internal;
using Xunit.Runner.Common;
using Xunit.Sdk;
using Xunit.v3;

// TODO: Because the engine has asynchronous state, many of the operations here probably need
// to wait for the engine to reach the Connected state before doing the requested operation.
// This includes metadata properties (like TestFrameworkDisplayName) as well as operations
// (like Find/Run).
namespace Xunit.Runner.v3
{
	/// <summary>
	/// This class be used to do discovery and execution of xUnit.net v3 tests.
	/// Runner authors are strongly encouraged to use <see cref="XunitFrontController"/>
	/// instead of using this class directly.
	/// </summary>
	public class Xunit3 : IFrontController
	{
		readonly _IMessageSink diagnosticMessageSink;
		readonly DisposalTracker disposalTracker = new();
		readonly ConcurrentDictionary<string, _IMessageSink> operations = new();
		readonly XunitProjectAssembly projectAssembly;
		readonly Process process;
		readonly TcpRunnerEngine runnerEngine;
		readonly _ISourceInformationProvider sourceInformationProvider;

		Xunit3(
			_IMessageSink diagnosticMessageSink,
			XunitProjectAssembly projectAssembly,
			_ISourceInformationProvider sourceInformationProvider)
		{
			this.diagnosticMessageSink = Guard.ArgumentNotNull(diagnosticMessageSink);
			this.projectAssembly = Guard.ArgumentNotNull(projectAssembly);
			this.sourceInformationProvider = Guard.ArgumentNotNull(sourceInformationProvider);

			Guard.NotNull($"{typeof(Xunit3).FullName} does not yet support dynamic assemblies", projectAssembly.AssemblyFileName);
			Guard.ArgumentValid("xUnit.net v3 tests do not support app domains", projectAssembly.Configuration.AppDomainOrDefault != AppDomainSupport.Required, nameof(projectAssembly));

			runnerEngine = new TcpRunnerEngine("tbd", OnMessage, diagnosticMessageSink);
			disposalTracker.Add(runnerEngine);

			var port = runnerEngine.Start();
			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"v3 TCP Server running on tcp://localhost:{port}/ for '{projectAssembly.AssemblyFileName}'" });

			var startInfo = new ProcessStartInfo
			{
				Arguments = $"-tcp {port}",
				CreateNoWindow = true,
				ErrorDialog = false,
				FileName = projectAssembly.AssemblyFileName,
				UseShellExecute = true,
				WindowStyle = ProcessWindowStyle.Hidden,
			};

			var workingDirectory = Path.GetDirectoryName(projectAssembly.AssemblyFileName);
			if (!string.IsNullOrWhiteSpace(workingDirectory))
				startInfo.WorkingDirectory = workingDirectory;

			process = Guard.NotNull("Got a null process from Process.Start", Process.Start(startInfo));
		}

		/// <inheritdoc/>
		public bool CanUseAppDomains => false;

		/// <inheritdoc/>
		public string TargetFramework => projectAssembly.TargetFramework;

		/// <inheritdoc/>
		public string TestAssemblyUniqueID
		{
			get
			{
				WaitForEngineReady();
				return runnerEngine.TestAssemblyUniqueID;
			}
		}

		/// <inheritdoc/>
		public string TestFrameworkDisplayName
		{
			get
			{
				WaitForEngineReady();
				return runnerEngine.TestFrameworkDisplayName;
			}
		}

		/// <inheritdoc/>
		public async ValueTask DisposeAsync()
		{
			await disposalTracker.DisposeAsync();

			if (!process.WaitForExit(5000))
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"Child process {process.Id} did not exit within 5 seconds; may need to be manually stopped" });
		}

		/// <inheritdoc/>
		// TODO: Utilize the settings
		public void Find(
			_IMessageSink messageSink,
			FrontControllerFindSettings settings)
		{
			WaitForEngineReady();

			var operationID = Guid.NewGuid().ToString("n");
			operations.TryAdd(operationID, messageSink);
			runnerEngine.SendFind(operationID);
		}

		/// <inheritdoc/>
		// TODO: Utilize the settings
		public void FindAndRun(
			_IMessageSink messageSink,
			FrontControllerFindAndRunSettings settings)
		{
			WaitForEngineReady();

			var operationID = Guid.NewGuid().ToString("n");
			operations.TryAdd(operationID, messageSink);
			runnerEngine.SendRun(operationID);
		}

		bool OnMessage(string operationID, _MessageSinkMessage message)
		{
			if (operationID == "::BROADCAST::")
			{
				foreach (var operationSink in operations.Values)
					operationSink.OnMessage(message);

				return true;
			}

			if (operations.TryGetValue(operationID, out var messageSink))
				return messageSink.OnMessage(message);

			return true;
		}

		/// <inheritdoc/>
		public void Run(
			_IMessageSink messageSink,
			FrontControllerRunSettings settings)
		{
			WaitForEngineReady();
			throw new NotImplementedException();
		}

		bool WaitForEngineReady(int milliseconds = 30000)
		{
			if (runnerEngine.State.HasReachedConnectedState())
				return true;

			return Task.Run(async () =>
			{
				var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(milliseconds));

				while (true)
				{
					if (cancellationTokenSource.IsCancellationRequested)
						return false;

					if (runnerEngine.State.HasReachedConnectedState())
						return true;

					await Task.Delay(50, cancellationTokenSource.Token);
				}
			}).ConfigureAwait(false).GetAwaiter().GetResult();
		}

		// Factory methods

		/// <summary>
		/// Returns an implementation of <see cref="IFrontControllerDiscoverer"/> which can be used
		/// to discover xUnit.net v3 tests, including source-based discovery.
		/// </summary>
		/// <param name="assemblyInfo">The assembly to use for discovery</param>
		/// <param name="projectAssembly">The test project assembly.</param>
		/// <param name="xunitExecutionAssemblyPath">The path on disk of xunit.execution.*.dll; if <c>null</c>, then
		/// the location of xunit.execution.*.dll is implied based on the location of the test assembly</param>
		/// <param name="sourceInformationProvider">The optional source information provider.</param>
		/// <param name="diagnosticMessageSink">The message sink which receives <see cref="_DiagnosticMessage"/> messages.</param>
		/// <param name="verifyAssembliesOnDisk">Determines whether or not to check for the existence of assembly files.</param>
		public static IFrontControllerDiscoverer ForDiscovery(
			_IAssemblyInfo assemblyInfo,
			XunitProjectAssembly projectAssembly,
			string? xunitExecutionAssemblyPath = null,
			_ISourceInformationProvider? sourceInformationProvider = null,
			_IMessageSink? diagnosticMessageSink = null,
			bool verifyAssembliesOnDisk = true)
		{
			throw new NotImplementedException();
			//var appDomainSupport = projectAssembly.Configuration.AppDomainOrDefault;

			//Guard.ArgumentNotNull(diagnosticMessageSink);
			//Guard.ArgumentNotNull(assemblyInfo);

			//return new Xunit2(
			//	diagnosticMessageSink,
			//	appDomainSupport,
			//	sourceInformationProvider ?? _NullSourceInformationProvider.Instance,  // TODO: Need to find a way to be able to use VisualStudioSourceInformationProvider
			//	assemblyInfo,
			//	assemblyFileName: null,
			//	xunitExecutionAssemblyPath ?? GetXunitExecutionAssemblyPath(appDomainSupport, assemblyInfo),
			//	projectAssembly.ConfigFilename,
			//	projectAssembly.Configuration.ShadowCopyOrDefault,
			//	projectAssembly.Configuration.ShadowCopyFolder,
			//	verifyAssembliesOnDisk
			//);
		}

		/// <summary>
		/// Returns an implementation of <see cref="IFrontController"/> which can be used
		/// for both discovery and execution of xUnit.net v3 tests.
		/// </summary>
		/// <param name="projectAssembly">The test project assembly.</param>
		/// <param name="sourceInformationProvider">The optional source information provider.</param>
		/// <param name="diagnosticMessageSink">The message sink which receives <see cref="_DiagnosticMessage"/> messages.</param>
		/// <param name="verifyAssembliesOnDisk">Determines whether or not to check for the existence of assembly files.</param>
		public static IFrontController ForDiscoveryAndExecution(
			XunitProjectAssembly projectAssembly,
			_ISourceInformationProvider? sourceInformationProvider = null,
			_IMessageSink? diagnosticMessageSink = null,
			bool verifyAssembliesOnDisk = true)
		{
			Guard.ArgumentNotNull(projectAssembly);

			var assemblyFileName = projectAssembly.AssemblyFileName;

			if (diagnosticMessageSink == null)
				diagnosticMessageSink = _NullMessageSink.Instance;

			if (verifyAssembliesOnDisk && assemblyFileName != null)
				Guard.FileExists(assemblyFileName, $"{nameof(projectAssembly)}.{nameof(XunitProjectAssembly.AssemblyFileName)}");

#if NETFRAMEWORK
			if (sourceInformationProvider == null && assemblyFileName != null)
				sourceInformationProvider = new VisualStudioSourceInformationProvider(assemblyFileName, diagnosticMessageSink);
#endif

			return new Xunit3(
				diagnosticMessageSink,
				projectAssembly,
				sourceInformationProvider ?? _NullSourceInformationProvider.Instance
			);
		}
	}
}
