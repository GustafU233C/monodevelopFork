//
// ObjectValueTreeViewController.cs
//
// Author:
//       gregm <gregm@microsoft.com>
//
// Copyright (c) 2019 Microsoft
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Mono.Debugging.Client;

using MonoDevelop.Core;

namespace MonoDevelop.Debugger
{
	/*
	 * Issues?
	 *
	 * - RemoveChildren did an unregister of events for child nodes that were removed, we might need to do the same for
	 * refreshing a node (which may replace it's children nodes)
	 * 
	 */
	public class ObjectValueTreeViewController
	{
		readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource ();
		public const int MaxEnumerableChildrenToFetch = 20;
		IDebuggerService debuggerService;
		PinnedWatch pinnedWatch;
		bool allowWatchExpressions;
		bool allowEditing;

		/// <summary>
		/// Holds a dictionary of tasks that are fetching children values of the given node
		/// </summary>
		readonly Dictionary<ObjectValueNode, Task<int>> childFetchTasks = new Dictionary<ObjectValueNode, Task<int>> ();

		// TODO: can we refactor this to a separate class?
		/// <summary>
		/// Holds a dictionary of arbitrary objects for nodes that are currently "Evaluating" by the debugger
		/// When the node has completed evaluation ValueUpdated event will be fired, passing the given object
		/// </summary>
		readonly Dictionary<ObjectValueNode, object> evaluationWatches = new Dictionary<ObjectValueNode, object> ();

		/// <summary>
		/// Holds a dictionary of node paths and the values. Used to show values that have changed from one frame to the next.
		/// </summary>
		readonly Dictionary<string, CheckpointState> oldValues = new Dictionary<string, CheckpointState> ();

		public ObjectValueTreeViewController ()
		{
			AllowPopupMenu = true;
			HeadersVisible = true;
		}

		public IDebuggerService Debugger {
			get {
				if (debuggerService == null) {
					debuggerService = OnGetDebuggerService ();
				}

				return debuggerService;
			}
		}

		public ObjectValueNode Root { get; private set; }

		public IStackFrame Frame { get; set; }

		/// <summary>
		/// Gets a value indicating whether the user should be able to edit values in the tree
		/// </summary>
		public bool AllowEditing {
			get => allowEditing;
			set {
				if (allowEditing == value)
					return;

				allowEditing = value;

				// trigger a refresh
				if (Root != null) {
					OnChildrenLoaded (Root, 0, Root.Children.Count);
				}
			}
		}

		/// <summary>
		/// Gets a value indicating whether or not the user should be able to expand nodes in the tree.
		/// </summary>
		public bool AllowExpanding {
			get; set;
		}

		/// <summary>
		/// Gets a value indicating whether the user should be able to add watch expressions to the tree
		/// </summary>
		public bool AllowWatchExpressions {
			get => allowWatchExpressions;
			set {
				if (allowWatchExpressions == value)
					return;

				allowWatchExpressions = value;

				// trigger a refresh
				if (Root != null) {
					OnChildrenLoaded (Root, 0, Root.Children.Count);
				}
			}
		}

		public bool AllowPopupMenu {
			get; set;
		}

		/// <summary>
		/// Gets a value indicating whether or not the TreeView should compact the view.
		/// </summary>
		public bool CompactView {
			get; set;
		}

		/// <summary>
		/// Gets a value indicating whether or not the table columns should be visible.
		/// </summary>
		public bool HeadersVisible {
			get; set;
		}

		#region Pinned Watches

		/// <summary>
		/// Gets a value indicating whether the user should be able to pin the value to the text editor.
		/// </summary>
		public bool AllowPinning {
			get; set;
		}

		public event EventHandler PinnedWatchChanged;

		public PinnedWatch PinnedWatch {
			get { return pinnedWatch; }
			set {
				if (pinnedWatch == value)
					return;

				pinnedWatch = value;

				PinnedWatchChanged?.Invoke (this, EventArgs.Empty);
			}
		}

		public string PinnedWatchFile {
			get; set;
		}

		public int PinnedWatchLine {
			get; set;
		}

		public bool RootPinAlwaysVisible {
			get; set;
		}

		#endregion

		public bool CanQueryDebugger {
			get {
				return Debugger.IsConnected && Debugger.IsPaused;
			}
		}

		public event EventHandler PinStatusChanged;

		void OnPinStatusChanged ()
		{
			PinStatusChanged?.Invoke (this, EventArgs.Empty);
		}

		public void CreatePinnedWatch (string expression, int height)
		{
			var watch = new PinnedWatch ();

			if (PinnedWatch != null) {
				watch.File = PinnedWatch.File;
				watch.Line = PinnedWatch.Line;
				watch.OffsetX = PinnedWatch.OffsetX;
				watch.OffsetY = PinnedWatch.OffsetY + height + 5;
			} else {
				watch.File = PinnedWatchFile;
				watch.Line = PinnedWatchLine;
				watch.OffsetX = -1; // means that the watch should be placed at the line coordinates defined by watch.Line
				watch.OffsetY = -1;
			}

			watch.Expression = expression;
			DebuggingService.PinnedWatches.Add (watch);

			OnPinStatusChanged ();
		}

		public void RemovePinnedWatch ()
		{
			DebuggingService.PinnedWatches.Remove (PinnedWatch);
			OnPinStatusChanged ();
		}

		public event EventHandler StartEditing;

		internal void OnStartEditing ()
		{
			StartEditing?.Invoke (this, EventArgs.Empty);
		}

		public event EventHandler EndEditing;

		internal void OnEndEditing ()
		{
			EndEditing?.Invoke (this, EventArgs.Empty);
		}

		public event EventHandler<ObjectValueNodeChildrenChangedEventArgs> ChildrenLoaded;

		/// <summary>
		/// NodeExpanded is fired when the node has expanded and the children
		/// for the node have been loaded and are in the node's children collection
		/// </summary>
		public event EventHandler<ObjectValueNodeEventArgs> NodeExpanded;

		/// <summary>
		/// EvaluationCompleted is fired when the debugger informs us that a node that
		/// was IsEvaluating has finished evaluating and the values of the node can
		/// be displaved
		/// </summary>
		public event EventHandler<ObjectValueNodeEvaluationCompletedEventArgs> EvaluationCompleted;

		public object GetControl ()
		{
			return new GtkObjectValueTreeView (this);
		}

		public void CancelAsyncTasks ()
		{
			cancellationTokenSource.Cancel ();
		}

		/// <summary>
		/// Clears the controller of nodes and resets the root to a new empty node
		/// </summary>
		public void ClearValues ()
		{
			Root = OnCreateRoot ();

			OnChildrenLoaded (Root, 0, Root.Children.Count);
		}

		/// <summary>
		/// Adds values to the root node, eg locals or watch expressions
		/// </summary>
		public void AddValue (ObjectValueNode value)
		{
			if (Root == null) {
				Root = OnCreateRoot ();
			}

			((RootObjectValueNode) Root).AddValue (value);
			RegisterNode (value);

			OnChildrenLoaded (Root, 0, Root.Children.Count);
		}

		/// <summary>
		/// Adds values to the root node, eg locals or watch expressions
		/// </summary>
		public void AddValues (IEnumerable<ObjectValueNode> values)
		{
			if (Root == null) {
				Root = OnCreateRoot ();
			}

			var nodes = values.ToList ();
			((RootObjectValueNode) Root).AddValues (nodes);

			// TODO: we want to enumerate just the once
			foreach (var node in nodes) {
				RegisterNode (node);
			}

			OnChildrenLoaded (Root, 0, Root.Children.Count);
		}

		public bool RemoveValue (ObjectValueNode node)
		{
			UnregisterNode (node);
			OnEvaluationCompleted (node, new ObjectValueNode[0]);

			return true;
		}

		/// <summary>
		/// Clear everything
		/// </summary>
		public void ClearAll ()
		{
			ClearEvaluationCompletionRegistrations ();
			ClearValues ();
		}

		// TODO: can we improve this
		public string GetDisplayValueWithVisualisers (ObjectValueNode node, out bool showViewerButton)
		{
			showViewerButton = false;
			if (node == null)
				return null;

			string result;
			showViewerButton = !node.IsNull && Debugger.HasValueVisualizers (node);

			if (!node.IsNull && Debugger.HasInlineVisualizer (node)) {
				try {
					result = node.GetInlineVisualisation ();
				} catch (Exception) {
					result = node.GetDisplayValue ();
				}
			} else {
				result = node.GetDisplayValue ();
			}

			return result;
		}

		#region Checkpoints
		public void ChangeCheckpoint ()
		{
			// clear old values,
			// iterate over all the nodes and store the values so we can compare
			// on the next update
			oldValues.Clear ();
			if (Root != null) {
				ChangeCheckpoint (Root);
			}
		}

		public void ResetChangeTracking ()
		{
			oldValues.Clear ();
		}

		/// <summary>
		/// Returns true if the value of the node is different from it's last value
		/// at the last checkpoint. Returns false if the node was not scanned at the
		/// last checkpoint
		/// </summary>
		public bool GetNodeHasChangedSinceLastCheckpoint (ObjectValueNode node)
		{
			if (oldValues.TryGetValue (node.Path, out CheckpointState checkpointState)) {
				return node.Value != checkpointState.Value;
			}

			return false;
		}

		/// <summary>
		/// Returns true if the node was expanded when the last checkpoint was made
		/// </summary>
		public bool GetNodeWasExpandedAtLastCheckpoint (ObjectValueNode node)
		{
			if (oldValues.TryGetValue (node.Path, out CheckpointState checkpointState)) {
				return checkpointState.Expanded;
			}

			return false;
		}
		#endregion

		#region Expressions

		public void AddExpression (string expression)
		{
			if (!AllowWatchExpressions)
				return;

			var node = Frame.EvaluateExpression (expression);
			AddValue (node);
		}

		public void AddExpressions (IList<string> expressions)
		{
			if (!AllowWatchExpressions)
				return;

			if (Frame != null) {
				var nodes = Frame.EvaluateExpressions (expressions);
				AddValues (nodes);
			}
		}

		public bool EditExpression(ObjectValueNode node, string newExpression)
		{
			if (node.Name == newExpression)
				return false;

			UnregisterNode (node);
			if (string.IsNullOrEmpty(newExpression)) {
				// we want the expression removed from the tree
				OnEvaluationCompleted (node, new ObjectValueNode[0]);
				return true;
			}

			var expressionNode = Frame.EvaluateExpression(newExpression);
			RegisterNode (expressionNode);
			OnEvaluationCompleted (node, new ObjectValueNode[1] { expressionNode });

			return true;
		}
		#endregion

		#region Editing
		/// <summary>
		/// Returns true if the node can be edited
		/// </summary>
		public bool CanEditObject (ObjectValueNode node)
		{
			if (AllowEditing) {
				// TODO: clean up
				if (node.IsUnknown) {
					if (Frame != null) {
						return false;
					}
				}

				return node.CanEdit;
			}

			return false;
		}

		/// <summary>
		/// Edits the value of the node and returns a value indicating whether the node's value changed from
		/// when the node was initially loaded from the debugger
		/// </summary>
		public bool EditNodeValue (ObjectValueNode node, string newValue)
		{
			if (node == null || !AllowEditing)
				return false;

			try {
				if (node.Value == newValue)
					return false;

				// make sure we set an old value for this node so we can show that it has changed
				if (!oldValues.TryGetValue (node.Path, out CheckpointState state)) {
					oldValues[node.Path] = new CheckpointState (node);
				}

				// ensure the parent and node are in the checkpoint and expanded
				// so that the tree expands the node we just edited when refreshed
				EnsureNodeIsExpandedInCheckpoint (node);

				node.SetValue (newValue);
			} catch (Exception ex) {
				LoggingService.LogError ($"Could not set value for object '{node.Name}'", ex);
				return false;
			}

			// now, refresh the parent
			var parent = node.Parent; /*FindNode (node.ParentId);*/
			if (parent != null) {
				parent.Refresh ();
				RegisterForEvaluationCompletion (parent, true);
			}

			// the locals pad, for example, will reload all the values once this is fired
			// prior to reloading, a new checkpoint will be made
			Debugger.NotifyVariableChanged ();

			return true;
		}

		public bool ShowNodeValueVisualizer (ObjectValueNode node)
		{
			if (node != null) {

				// make sure we set an old value for this node so we can show that it has changed
				if (!oldValues.TryGetValue (node.Path, out CheckpointState state)) {
					oldValues[node.Path] = new CheckpointState (node);
				}

				// ensure the parent and node are in the checkpoint and expanded
				// so that the tree expands the node we just edited when refreshed
				EnsureNodeIsExpandedInCheckpoint (node);

				if (Debugger.ShowValueVisualizer (node)) {
					// the value of the node changed so now refresh the parent
					var parent = node.Parent; /*FindNode (node.ParentId);*/
					if (parent != null) {
						parent.Refresh ();
						RegisterForEvaluationCompletion (parent, true);
					}

					return true;
				}
			}

			return false;
		}

		void EnsureNodeIsExpandedInCheckpoint(ObjectValueNode node)
		{
			var parent = node.Parent; /*FindNode (node.ParentId);*/

			while (parent != null && parent != Root) {
				if (oldValues.TryGetValue (parent.Path, out CheckpointState state)) {
					state.Expanded = true;
				} else {
					oldValues[parent.Path] = new CheckpointState (parent) { Expanded = true };
				}

				parent = parent.Parent; /*FindNode (parent.ParentId);*/
			}
		}
		#endregion

		public void RefreshNode (ObjectValueNode node)
		{
			if (node == null)
				return;

			if (CanQueryDebugger && Frame != null) {
				UnregisterForEvaluationCompletion (node);

				var options = Frame.CloneSessionEvaluationOpions ();
				options.AllowMethodEvaluation = true;
				options.AllowToStringCalls = true;
				options.AllowTargetInvoke = true;
				options.EllipsizeStrings = false;

				//string oldName = val.Name;
				node.Refresh (options);

				// TODO: this is for watched expressions
				// Don't update the name for the values entered by the user
				//if (store.IterDepth (iter) == 0)
				//	val.Name = oldName;

				RegisterForEvaluationCompletion (node);
			}
		}

		#region Fetching and loading children
		/// <summary>
		/// Marks a node as expanded and fetches children for the node if they have not been already fetched
		/// </summary>
		public async Task ExpandNodeAsync (ObjectValueNode node)
		{
			// if we think the node is expanded already, no need to trigger this again
			if (node.IsExpanded)
				return;

			node.IsExpanded = true;

			int loadedCount = 0;
			if (node.IsEnumerable) {
				// if we already have some loaded, don't load more - that is a specific user gesture
				if (node.Children.Count == 0) {
					// page the children in, instead of loading them all at once
					loadedCount = await FetchChildrenAsync (node, MaxEnumerableChildrenToFetch, cancellationTokenSource.Token);
				}
			} else {
				loadedCount = await FetchChildrenAsync (node, 0, cancellationTokenSource.Token);
			}

			if (loadedCount > 0) {
				OnChildrenLoaded (node, 0, node.Children.Count);
			}

			OnNodeExpanded (node);
		}

		/// <summary>
		/// Marks a node as not expanded
		/// </summary>
		public void CollapseNode (ObjectValueNode node)
		{
			node.IsExpanded = false;
		}

		public async Task<int> FetchMoreChildrenAsync (ObjectValueNode node)
		{
			if (node.ChildrenLoaded) {
				return 0;
			}

			try {
				if (childFetchTasks.TryGetValue (node, out Task<int> task)) {
					// there is already a task to fetch the children
					return await task;
				} else {
					try {
						var oldCount = node.Children.Count;
						var result = await node.LoadChildrenAsync (MaxEnumerableChildrenToFetch, cancellationTokenSource.Token);

						// if any of them are still evaluating register for
						// a completion event so that we can tell the UI
						for (int i = oldCount; i < oldCount + result; i++) {
							var c = node.Children [i];
							RegisterNode (c);
						}

						// always send the event so that the UI can determine if the node has finished loading.
						OnChildrenLoaded (node, oldCount, result);

						return result;
					} finally {
						childFetchTasks.Remove (node);
					}
				}
			} catch (Exception ex) {
				// TODO: log or fail?
			}

			return 0;
		}

		/// <summary>
		/// Fetches the child nodes and returns the count of new children that were loaded.
		/// The children will be in node.Children.
		/// </summary>
		async Task<int> FetchChildrenAsync (ObjectValueNode node, int count, CancellationToken cancellationToken)
		{
			if (node.ChildrenLoaded) {
				return 0;
			}

			try {
				if (childFetchTasks.TryGetValue (node, out Task<int> task)) {
					// there is already a task to fetch the children
					return await task;
				} else {
					try {
						int result = 0;
						if (count > 0) {
							var oldCount = node.Children.Count;
							result = await node.LoadChildrenAsync (count, cancellationToken);

							// if any of them are still evaluating register for
							// a completion event so that we can tell the UI
							for (int i = oldCount; i < oldCount + result; i++) {
								var c = node.Children [i];
								RegisterNode (c);
							}
						} else {
							result = await node.LoadChildrenAsync (cancellationToken);

							// if any of them are still evaluating register for
							// a completion event so that we can tell the UI
							foreach (var c in node.Children) {
								RegisterNode (c);
							}
						}

						return result;
					} finally {
						childFetchTasks.Remove (node);
					}
				}
			} catch (Exception ex) {
				// TODO: log or fail?
			}

			return 0;
		}
		#endregion

		#region Evaluation watches
		/// <summary>
		/// Registers the ValueChanged event for a node where IsEvaluating is true. If the node is not evaluating, and
		/// sendImmediatelyIfNotEvaulating is true, then fire OnEvaluatingNodeValueChanged immediately 
		/// </summary>
		void RegisterForEvaluationCompletion (ObjectValueNode node, bool sendImmediatelyIfNotEvaulating = false)
		{
			if (node.IsEvaluating) {
				evaluationWatches [node] = null;
				node.ValueChanged += OnEvaluatingNodeValueChanged;
			} else if (sendImmediatelyIfNotEvaulating) {
				OnEvaluatingNodeValueChanged (node, EventArgs.Empty);
			}
		}

		/// <summary>
		/// Removes the ValueChanged handler from the node
		/// </summary>
		void UnregisterForEvaluationCompletion (ObjectValueNode node)
		{
			if (node != null) {
				node.ValueChanged -= OnEvaluatingNodeValueChanged;
				evaluationWatches.Remove (node);
			}
		}

		/// <summary>
		/// Removes all ValueChanged handlers for evaluating nodes
		/// </summary>
		void ClearEvaluationCompletionRegistrations ()
		{
			foreach (var node in evaluationWatches.Keys) {
				node.ValueChanged -= OnEvaluatingNodeValueChanged;
			}

			evaluationWatches.Clear ();
		}

		#endregion


		/// <summary>
		/// Called when clearing, by default sets the root to a new ObjectValueNode
		/// </summary>
		protected virtual ObjectValueNode OnCreateRoot ()
		{
			return new RootObjectValueNode ();
		}

		protected virtual IDebuggerService OnGetDebuggerService ()
		{
			return new ObjectValueDebuggerService ();
		}

		/// <summary>
		/// Registers the node in the index and sets a watch for evaluating nodes
		/// </summary>
		void RegisterNode (ObjectValueNode node)
		{
			if (node != null) {
				RegisterForEvaluationCompletion (node);
			}
		}

		void UnregisterNode (ObjectValueNode node)
		{
			if (node != null) {
				UnregisterForEvaluationCompletion (node);
			}
		}

		/// <summary>
		/// Creates a checkpoint of the value of the node and any children that are expanded
		/// </summary>
		void ChangeCheckpoint (ObjectValueNode node)
		{
			oldValues[node.Path] = new CheckpointState (node);

			if (node.IsExpanded) {
				foreach (var child in node.Children) {
					ChangeCheckpoint (child);
				}
			}
		}

		#region Event triggers
		void OnChildrenLoaded (ObjectValueNode node, int index, int count)
		{
			ChildrenLoaded?.Invoke (this, new ObjectValueNodeChildrenChangedEventArgs (node, index, count));
		}

		/// <summary>
		/// Triggered in response to ValueChanged on a node
		/// </summary>
		void OnEvaluatingNodeValueChanged (object sender, EventArgs e)
		{
			if (sender is ObjectValueNode node) {
				UnregisterForEvaluationCompletion (node);

				if (sender is IEvaluatingGroupObjectValueNode evalGroupNode) {
					if (evalGroupNode.IsEvaluatingGroup) {
						var replacementNodes = evalGroupNode.GetEvaluationGroupReplacementNodes ();

						foreach (var newNode in replacementNodes) {
							RegisterNode (newNode);
						}

						// TODO: we could improve how we notify this and pass child indexes as well
						OnEvaluationCompleted (sender as ObjectValueNode, replacementNodes);
					} else {
						OnEvaluationCompleted (sender as ObjectValueNode);
					}
				} else {
					OnEvaluationCompleted (sender as ObjectValueNode);
				}
			}
		}

		void OnEvaluationCompleted (ObjectValueNode node)
		{
			EvaluationCompleted?.Invoke (this, new ObjectValueNodeEvaluationCompletedEventArgs (node, new ObjectValueNode [1] { node }));
		}

		void OnEvaluationCompleted (ObjectValueNode node, ObjectValueNode [] replacementNodes)
		{
			// `node` returns us a set of new nodes that need to be replaced into the children
			// of node.parent. This should only be applicable to direct children of the root since
			// this construct is to support placehold values for "locals" etc
			if (node.Parent is ISupportChildObjectValueNodeReplacement replacerParent) {
				replacerParent.ReplaceChildNode (node, replacementNodes);
			}

			EvaluationCompleted?.Invoke (this, new ObjectValueNodeEvaluationCompletedEventArgs (node, replacementNodes));
		}

		void OnNodeExpanded (ObjectValueNode node)
		{
			NodeExpanded?.Invoke (this, new ObjectValueNodeEventArgs (node));
		}
		#endregion

		class CheckpointState
		{
			public CheckpointState (ObjectValueNode node)
			{
				Expanded = node.IsExpanded;
				Value = node.Value;
			}

			public bool Expanded { get; set; }
			public string Value { get; set; }
		}

		public static string GetIcon (ObjectValueFlags flags)
		{
			if ((flags & ObjectValueFlags.Field) != 0 && (flags & ObjectValueFlags.ReadOnly) != 0)
				return "md-literal";

			string global = (flags & ObjectValueFlags.Global) != 0 ? "static-" : string.Empty;
			string source;

			switch (flags & ObjectValueFlags.OriginMask) {
			case ObjectValueFlags.Property: source = "property"; break;
			case ObjectValueFlags.Type: source = "class"; global = string.Empty; break;
			case ObjectValueFlags.Method: source = "method"; break;
			case ObjectValueFlags.Literal: return "md-literal";
			case ObjectValueFlags.Namespace: return "md-name-space";
			case ObjectValueFlags.Group: return "md-open-resource-folder";
			case ObjectValueFlags.Field: source = "field"; break;
			case ObjectValueFlags.Variable: return "md-variable";
			default: return "md-empty";
			}

			string access;
			switch (flags & ObjectValueFlags.AccessMask) {
			case ObjectValueFlags.Private: access = "private-"; break;
			case ObjectValueFlags.Internal: access = "internal-"; break;
			case ObjectValueFlags.InternalProtected:
			case ObjectValueFlags.Protected: access = "protected-"; break;
			default: access = string.Empty; break;
			}

			return "md-" + access + global + source;
		}
	}

	#region Extension methods and helpers
	/// <summary>
	/// Helper class to mimic existing API
	/// </summary>
	public static class ObjectValueTreeViewControllerExtensions
	{
		public static void SetStackFrame (this ObjectValueTreeViewController controller, StackFrame frame)
		{
			controller.Frame = new ProxyStackFrame (frame);
		}

		public static StackFrame GetStackFrame (this ObjectValueTreeViewController controller)
		{
			return (controller.Frame as ProxyStackFrame)?.StackFrame;
		}

		public static void AddValue (this ObjectValueTreeViewController controller, ObjectValue value)
		{
			controller.AddValue (new DebuggerObjectValueNode (value));
		}

		public static void AddValues (this ObjectValueTreeViewController controller, IEnumerable<ObjectValue> values)
		{
			controller.AddValues (values.Select (value => new DebuggerObjectValueNode (value)));
		}

		public static string[] GetExpressions (this ObjectValueTreeViewController controller)
		{
			// given that expressions are only supported by themselves (ie not mixed with locals for example)
			// and they are all children of the root, we can mimic a list of expressions by just grabbing the
			// name property of the root children
			if (controller.Root == null)
				return new string [0];

			return controller.Root.Children.Select (c => c.Name).ToArray ();
		}
	}

	public static class ObjectValueNodeExtensions
	{
		public static string GetDisplayValue (this ObjectValueNode node)
		{
			if (node.DisplayValue == null)
				return "(null)";

			if (node.DisplayValue.Length > 1000) {
				// Truncate the string to stop the UI from hanging
				// when calculating the size for very large amounts
				// of text.
				return node.DisplayValue.Substring (0, 1000) + "…";
			}

			return node.DisplayValue;
		}

		public static ObjectValue GetDebuggerObjectValue (this ObjectValueNode node)
		{
			if (node != null && node is DebuggerObjectValueNode val) {
				return val.DebuggerObject;
			}

			return null;
		}

		public static bool GetIsEvaluatingGroup (this ObjectValueNode node)
		{
			return (node is IEvaluatingGroupObjectValueNode evg && evg.IsEvaluatingGroup);
		}

		public static string GetInlineVisualisation (this ObjectValueNode node)
		{
			// TODO: this is not possible to mock as it is
			if (node is DebuggerObjectValueNode val) {
				return DebuggingService.GetInlineVisualizer (val.DebuggerObject).InlineVisualize (val.DebuggerObject);
			}

			return node.GetDisplayValue ();
		}
	}
	#endregion
}
