﻿//
// ExtensionContext.cs
//
// Author:
//   Lluis Sanchez Gual
//
// Copyright (C) 2007 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using Mono.Addins.Description;

namespace Mono.Addins
{
    /// <summary>
    /// An extension context.
    /// </summary>
    /// <remarks>
    /// Extension contexts can be used to query the extension tree
    /// using particular condition values. Extension points which
    /// declare the availability of a condition type can only be
    /// queryed using an extension context which provides an
    /// evaluator for that condition.
    /// </remarks>
    public class ExtensionContext
	{
		internal object LocalLock = new object ();

		ImmutableDictionary<string, ConditionInfo> conditionTypes = ImmutableDictionary<string, ConditionInfo>.Empty;
		ImmutableDictionary<BaseCondition, ImmutableArray<TreeNode>> conditionsToNodes = ImmutableDictionary<BaseCondition, ImmutableArray<TreeNode>>.Empty;
		ImmutableArray<WeakReference> childContexts = ImmutableArray<WeakReference>.Empty;
		ExtensionContext parentContext;
		ExtensionTree tree;

		ImmutableArray<string> runTimeEnabledAddins = ImmutableArray<string>.Empty;
		ImmutableArray<string> runTimeDisabledAddins = ImmutableArray<string>.Empty;
		
		/// <summary>
		/// Extension change event.
		/// </summary>
		/// <remarks>
		/// This event is fired when any extension point in the add-in system changes.
		/// The event args object provides the path of the changed extension, although
		/// it does not provide information about what changed. Hosts subscribing to
		/// this event should get the new list of nodes using a query method such as
		/// AddinManager.GetExtensionNodes() and then update whatever needs to be updated.
		/// </remarks>
		public event ExtensionEventHandler ExtensionChanged;
		
		internal void Initialize (AddinEngine addinEngine)
		{
			tree = new ExtensionTree (addinEngine, this);
		}

#pragma warning disable 1591
		[ObsoleteAttribute]
		protected void Clear ()
		{
		}
#pragma warning restore 1591

		
		internal void ClearContext ()
		{
			conditionTypes = conditionTypes.Clear ();
			conditionsToNodes.Clear ();
			childContexts = ImmutableArray<WeakReference>.Empty;
			parentContext = null;
			tree = null;
			runTimeEnabledAddins = ImmutableArray<string>.Empty;
			runTimeDisabledAddins = ImmutableArray<string>.Empty;
		}
		
		internal AddinEngine AddinEngine {
			get { return tree.AddinEngine; }
		}

		void CleanDisposedChildContexts ()
		{
			var list = childContexts;
			List<WeakReference> toRemove = null;

			for (int n = 0; n < list.Length; n++) {
				if (list [n].Target == null) {
					// Create the list only if there is something to remove
					if (toRemove == null)
						toRemove = new List<WeakReference> ();
					toRemove.Add (list [n]);
				}
			}
			if (toRemove != null) {
				// Removing the stale contexts is not urgent, so if the lock can't be acquired now
				// it is ok to just skip the clean up and try later
				if (Monitor.TryEnter(LocalLock)) {
					try {
						childContexts = childContexts.RemoveRange (toRemove);
					} finally {
						Monitor.Exit (LocalLock);
					}
				}
			}
		}
		
		internal virtual void ResetCachedData ()
		{
			using (var transaction = BeginTransaction ())
				tree.ResetCachedData (transaction);

			foreach (var ctx in GetActiveChildContexes())
				ctx.ResetCachedData ();
		}
		
		internal ExtensionContext CreateChildContext ()
		{
			ExtensionContext ctx = new ExtensionContext ();
			ctx.Initialize (AddinEngine);
			ctx.parentContext = this;
			WeakReference wref = new WeakReference (ctx);

			lock (LocalLock) {
				CleanDisposedChildContexts ();
				childContexts = childContexts.Add (wref);
				return ctx;
			}
		}

		/// <summary>
		/// Registers a new condition in the extension context.
		/// </summary>
		/// <param name="id">
		/// Identifier of the condition.
		/// </param>
		/// <param name="type">
		/// Condition evaluator.
		/// </param>
		/// <remarks>
		/// The registered condition will be particular to this extension context.
		/// Any event that might be fired as a result of changes in the condition will
		/// only be fired in this context.
		/// </remarks>
		public void RegisterCondition (string id, ConditionType type)
		{
			using var transaction = BeginTransaction ();
			type.Id = id;
			GetOrCreateConditionInfo (transaction, id, type);
		}
		
		/// <summary>
		/// Registers a new condition in the extension context.
		/// </summary>
		/// <param name="id">
		/// Identifier of the condition.
		/// </param>
		/// <param name="type">
		/// Type of the condition evaluator. Must be a subclass of Mono.Addins.ConditionType.
		/// </param>
		/// <remarks>
		/// The registered condition will be particular to this extension context. Any event
		/// that might be fired as a result of changes in the condition will only be fired in this context.
		/// </remarks>
		public void RegisterCondition (string id, Type type)
		{
			using var transaction = BeginTransaction ();

			// Allows delayed creation of condition types
			GetOrCreateConditionInfo (transaction, id, type);
		}

		void RegisterCondition (ExtensionContextTransaction transaction, string id, Type type)
		{
			// Allows delayed creation of condition types
			GetOrCreateConditionInfo (transaction, id, type);
		}

		internal void RegisterCondition (ExtensionContextTransaction transaction, string id, RuntimeAddin addin, string typeName)
		{
			// Allows delayed creation of condition types
			GetOrCreateConditionInfo (transaction, id, new ConditionTypeData {
				TypeName = typeName,
				Addin = addin
			});
		}

		ConditionInfo GetOrCreateConditionInfo (ExtensionContextTransaction transaction, string id, object conditionTypeObject)
		{
			if (!conditionTypes.TryGetValue (id, out var info)) {
				info = new ConditionInfo ();
				info.CondType = conditionTypeObject;
				conditionTypes = conditionTypes.Add (id, info);
			} else {
				var oldType = info.CondType as ConditionType;
				info.CondType = conditionTypeObject;
				if (oldType != null)
					oldType.Changed -= new EventHandler (OnConditionChanged);
			}
			if (conditionTypeObject is ConditionType conditionType)
				conditionType.Changed += new EventHandler (OnConditionChanged);
			return info;
		}
		
		internal ConditionType GetCondition (string id)
		{
			if (conditionTypes.TryGetValue(id, out var info)) {
				if (info.CondType is ConditionType condition) {
					return condition;
				}
				else {
					// The condition needs to be instantiated in a lock, to avoid duplicate
					// creation if two threads are trying to get it

					lock (LocalLock) {

						// Check again from inside the lock (maybe another thread already created the condition)
						if (info.CondType is ConditionType cond)
							return cond;

						// The condition was registered as a type, create an instance now

						Type type;
						if (info.CondType is ConditionTypeData data) {
							type = data.Addin.GetType (data.TypeName, true);
						} else
							type = info.CondType as Type;

						if (type != null) {
							var ct = (ConditionType)Activator.CreateInstance (type);
							ct.Id = id;
							ct.Changed += new EventHandler (OnConditionChanged);
							info.CondType = ct;
							return ct;
						}
					}
				}
			}
			
			if (parentContext != null)
				return parentContext.GetCondition (id);
			else
				return null;
		}

		/// <summary>
		/// Registers a set of node conditions
		/// </summary>
		internal void BulkRegisterNodeConditions (ExtensionContextTransaction transaction, IEnumerable<(TreeNode Node, BaseCondition Condition)> nodeConditions)
		{
			// We are going to do many changes, to create a builder for the dictionary
			var dictBuilder = conditionsToNodes.ToBuilder ();

			// Group nodes by the conditions, so that all nodes for a conditions can be processed together

			foreach (var group in nodeConditions.GroupBy (c => c.Condition)) {
				var condition = group.Key;

				if (!dictBuilder.TryGetValue (condition, out var list)) {

					// Condition not yet registered, register it now

					// Get a list of conditions on which this one depends
					var conditionTypeIds = new List<string> ();
					condition.GetConditionTypes (conditionTypeIds);

					foreach (string cid in conditionTypeIds) {

						// For each condition on which 'condition' depends, register the dependency
						// so that it if the condition changes, the dependencies are notified
						ConditionInfo info = GetOrCreateConditionInfo (transaction, cid, null);
						if (info.BoundConditions == null)
							info.BoundConditions = new List<BaseCondition> ();

						info.BoundConditions.Add (condition);
					}
					list = ImmutableArray<TreeNode>.Empty;
				}

				dictBuilder [condition] = list.AddRange (group.Select (item => item.Node));
			}
			conditionsToNodes = dictBuilder.ToImmutable ();
		}

		/// <summary>
		/// Unregisters a set of node conditions
		/// </summary>
		internal void BulkUnregisterNodeConditions (ExtensionContextTransaction transaction, IEnumerable<(TreeNode Node, BaseCondition Condition)> nodeConditions)
		{
			ImmutableDictionary<BaseCondition, ImmutableArray<TreeNode>>.Builder dictBuilder = null;

			foreach (var group in nodeConditions.GroupBy (c => c.Condition)) {
				var condition = group.Key;
				if (!conditionsToNodes.TryGetValue (condition, out var list))
					continue;

				var newList = list.RemoveRange (group.Select (item => item.Node));

				// If there are no changes, continue, no need to create the dictionary builder
				if (newList == list)
					continue;

				if (dictBuilder == null)
					dictBuilder = conditionsToNodes.ToBuilder ();

				if (newList.Length == 0) {

					// The condition is not used anymore. Remove it from the dictionary
                    // and unregister it from any condition it was bound to

					dictBuilder.Remove (condition);
					var conditionTypeIds = new List<string> ();
					condition.GetConditionTypes (conditionTypeIds);
					foreach (string cid in conditionTypeIds) {
						var info = conditionTypes [cid];
						if (info != null && info.BoundConditions != null)
							info.BoundConditions.Remove (condition);
					}
				} else
					dictBuilder [condition] = newList;
			}
			if (dictBuilder != null)
				conditionsToNodes = dictBuilder.ToImmutable ();
		}

		/// <summary>
		/// Returns the extension node in a path
		/// </summary>
		/// <param name="path">
		/// Location of the node.
		/// </param>
		/// <returns>
		/// The node, or null if not found.
		/// </returns>
		public ExtensionNode GetExtensionNode (string path)
		{
			TreeNode node = GetNode (path);
			if (node == null)
				return null;
			
			if (node.Condition == null || node.Condition.Evaluate (this))
				return node.ExtensionNode;
			else
				return null;
		}
		
		/// <summary>
		/// Returns the extension node in a path
		/// </summary>
		/// <param name="path">
		/// Location of the node.
		/// </param>
		/// <returns>
		/// The node, or null if not found.
		/// </returns>
		public T GetExtensionNode<T> (string path) where T: ExtensionNode
		{
			return (T) GetExtensionNode (path);
		}
		
		/// <summary>
		/// Gets extension nodes registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.>
		/// </param>
		/// <returns>
		/// All nodes registered in the provided path.
		/// </returns>
		public ExtensionNodeList GetExtensionNodes (string path)
		{
			return GetExtensionNodes (path, null);
		}
		
		/// <summary>
		/// Gets extension nodes registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <returns>
		/// A list of nodes
		/// </returns>
		/// <remarks>
		/// This method returns all nodes registered under the provided path.
		/// It will throw a InvalidOperationException if the type of one of
		/// the registered nodes is not assignable to the provided type.
		/// </remarks>
		public ExtensionNodeList<T> GetExtensionNodes<T> (string path) where T: ExtensionNode
		{
			ExtensionNodeList nodes = GetExtensionNodes (path, typeof(T));
			return new ExtensionNodeList<T> (nodes.list);
		}
		
		/// <summary>
		/// Gets extension nodes for a type extension point
		/// </summary>
		/// <param name="instanceType">
		/// Type defining the extension point
		/// </param>
		/// <returns>
		/// A list of nodes
		/// </returns>
		/// <remarks>
		/// This method returns all extension nodes bound to the provided type.
		/// </remarks>
		public ExtensionNodeList GetExtensionNodes (Type instanceType)
		{
			return GetExtensionNodes (instanceType, typeof(ExtensionNode));
		}
		
		/// <summary>
		/// Gets extension nodes for a type extension point
		/// </summary>
		/// <param name="instanceType">
		/// Type defining the extension point
		/// </param>
		/// <param name="expectedNodeType">
		/// Expected extension node type
		/// </param>
		/// <returns>
		/// A list of nodes
		/// </returns>
		/// <remarks>
		/// This method returns all nodes registered for the provided type.
		/// It will throw a InvalidOperationException if the type of one of
		/// the registered nodes is not assignable to the provided node type.
		/// </remarks>
		public ExtensionNodeList GetExtensionNodes (Type instanceType, Type expectedNodeType)
		{
			string path = AddinEngine.GetAutoTypeExtensionPoint (instanceType);
			if (path == null)
				return ExtensionNodeList.Empty;
			return GetExtensionNodes (path, expectedNodeType);
		}
		
		/// <summary>
		/// Gets extension nodes for a type extension point
		/// </summary>
		/// <param name="instanceType">
		/// Type defining the extension point
		/// </param>
		/// <returns>
		/// A list of nodes
		/// </returns>
		/// <remarks>
		/// This method returns all nodes registered for the provided type.
		/// It will throw a InvalidOperationException if the type of one of
		/// the registered nodes is not assignable to the specified node type argument.
		/// </remarks>
		public ExtensionNodeList<T> GetExtensionNodes<T> (Type instanceType) where T: ExtensionNode
		{
			string path = AddinEngine.GetAutoTypeExtensionPoint (instanceType);
			if (path == null)
				return new ExtensionNodeList<T> (null);
			return new ExtensionNodeList<T> (GetExtensionNodes (path, typeof (T)).list);
		}
		
		/// <summary>
		/// Gets extension nodes registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <param name="expectedNodeType">
		/// Expected node type.
		/// </param>
		/// <returns>
		/// A list of nodes
		/// </returns>
		/// <remarks>
		/// This method returns all nodes registered under the provided path.
		/// It will throw a InvalidOperationException if the type of one of
		/// the registered nodes is not assignable to the provided type.
		/// </remarks>
		public ExtensionNodeList GetExtensionNodes (string path, Type expectedNodeType)
		{
			TreeNode node = GetNode (path);
			if (node == null || !node.HasExtensionNode)
				return ExtensionNodeList.Empty;
			
			ExtensionNodeList list = node.ExtensionNode.ChildNodes;
			
			if (expectedNodeType != null) {
				bool foundError = false;
				foreach (ExtensionNode cnode in list) {
					if (!expectedNodeType.IsInstanceOfType (cnode)) {
						foundError = true;
						AddinEngine.ReportError ("Error while getting nodes for path '" + path + "'. Expected subclass of node type '" + expectedNodeType + "'. Found '" + cnode.GetType (), null, null, false);
					}
				}
				if (foundError) {
					// Create a new list excluding the elements that failed the test
					List<ExtensionNode> newList = new List<ExtensionNode> ();
					foreach (ExtensionNode cnode in list) {
						if (expectedNodeType.IsInstanceOfType (cnode))
							newList.Add (cnode);
					}
					return new ExtensionNodeList (newList);
				}
			}
			return list;
		}
		
		/// <summary>
		/// Gets extension objects registered for a type extension point.
		/// </summary>
		/// <param name="instanceType">
		/// Type defining the extension point
		/// </param>
		/// <returns>
		/// A list of objects
		/// </returns>
		public object[] GetExtensionObjects (Type instanceType)
		{
			return GetExtensionObjects (instanceType, true);
		}
		
		/// <summary>
		/// Gets extension objects registered for a type extension point.
		/// </summary>
		/// <returns>
		/// A list of objects
		/// </returns>
		/// <remarks>
		/// The type argument of this generic method is the type that defines
		/// the extension point.
		/// </remarks>
		public T[] GetExtensionObjects<T> ()
		{
			return GetExtensionObjects<T> (true);
		}
		
		/// <summary>
		/// Gets extension objects registered for a type extension point.
		/// </summary>
		/// <param name="instanceType">
		/// Type defining the extension point
		/// </param>
		/// <param name="reuseCachedInstance">
		/// When set to True, it will return instances created in previous calls.
		/// </param>
		/// <returns>
		/// A list of extension objects.
		/// </returns>
		public object[] GetExtensionObjects (Type instanceType, bool reuseCachedInstance)
		{
			string path = AddinEngine.GetAutoTypeExtensionPoint (instanceType);
			if (path == null)
				return (object[]) Array.CreateInstance (instanceType, 0);
			return GetExtensionObjects (path, instanceType, reuseCachedInstance);
		}
		
		/// <summary>
		/// Gets extension objects registered for a type extension point.
		/// </summary>
		/// <param name="reuseCachedInstance">
		/// When set to True, it will return instances created in previous calls.
		/// </param>
		/// <returns>
		/// A list of extension objects.
		/// </returns>
		/// <remarks>
		/// The type argument of this generic method is the type that defines
		/// the extension point.
		/// </remarks>
		public T[] GetExtensionObjects<T> (bool reuseCachedInstance)
		{
			string path = AddinEngine.GetAutoTypeExtensionPoint (typeof(T));
			if (path == null)
				return new T[0];
			return GetExtensionObjects<T> (path, reuseCachedInstance);
		}
		
		/// <summary>
		/// Gets extension objects registered in a path
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <returns>
		/// An array of objects registered in the path.
		/// </returns>
		/// <remarks>
		/// This method can only be used if all nodes in the provided extension path
		/// are of type Mono.Addins.TypeExtensionNode. The returned array is composed
		/// by all objects created by calling the TypeExtensionNode.CreateInstance()
		/// method for each node.
		/// </remarks>
		public object[] GetExtensionObjects (string path)
		{
			return GetExtensionObjects (path, typeof(object), true);
		}
		
		/// <summary>
		/// Gets extension objects registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <param name="reuseCachedInstance">
		/// When set to True, it will return instances created in previous calls.
		/// </param>
		/// <returns>
		/// An array of objects registered in the path.
		/// </returns>
		/// <remarks>
		/// This method can only be used if all nodes in the provided extension path
		/// are of type Mono.Addins.TypeExtensionNode. The returned array is composed
		/// by all objects created by calling the TypeExtensionNode.CreateInstance()
		/// method for each node (or TypeExtensionNode.GetInstance() if
		/// reuseCachedInstance is set to true)
		/// </remarks>
		public object[] GetExtensionObjects (string path, bool reuseCachedInstance)
		{
			return GetExtensionObjects (path, typeof(object), reuseCachedInstance);
		}
		
		/// <summary>
		/// Gets extension objects registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <param name="arrayElementType">
		/// Type of the return array elements.
		/// </param>
		/// <returns>
		/// An array of objects registered in the path.
		/// </returns>
		/// <remarks>
		/// This method can only be used if all nodes in the provided extension path
		/// are of type Mono.Addins.TypeExtensionNode. The returned array is composed
		/// by all objects created by calling the TypeExtensionNode.CreateInstance()
		/// method for each node.
		/// 
		/// An InvalidOperationException exception is thrown if one of the found
		/// objects is not a subclass of the provided type.
		/// </remarks>
		public object[] GetExtensionObjects (string path, Type arrayElementType)
		{
			return GetExtensionObjects (path, arrayElementType, true);
		}
		
		/// <summary>
		/// Gets extension objects registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <returns>
		/// An array of objects registered in the path.
		/// </returns>
		/// <remarks>
		/// This method can only be used if all nodes in the provided extension path
		/// are of type Mono.Addins.TypeExtensionNode. The returned array is composed
		/// by all objects created by calling the TypeExtensionNode.CreateInstance()
		/// method for each node.
		/// 
		/// An InvalidOperationException exception is thrown if one of the found
		/// objects is not a subclass of the provided type.
		/// </remarks>
		public T[] GetExtensionObjects<T> (string path)
		{
			return GetExtensionObjects<T> (path, true);
		}
		
		/// <summary>
		/// Gets extension objects registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <param name="reuseCachedInstance">
		/// When set to True, it will return instances created in previous calls.
		/// </param>
		/// <returns>
		/// An array of objects registered in the path.
		/// </returns>
		/// <remarks>
		/// This method can only be used if all nodes in the provided extension path
		/// are of type Mono.Addins.TypeExtensionNode. The returned array is composed
		/// by all objects created by calling the TypeExtensionNode.CreateInstance()
		/// method for each node (or TypeExtensionNode.GetInstance() if
		/// reuseCachedInstance is set to true).
		/// 
		/// An InvalidOperationException exception is thrown if one of the found
		/// objects is not a subclass of the provided type.
		/// </remarks>
		public T[] GetExtensionObjects<T> (string path, bool reuseCachedInstance)
		{
			ExtensionNode node = GetExtensionNode (path);
			if (node == null)
				throw new InvalidOperationException ("Extension node not found in path: " + path);
			return node.GetChildObjects<T> (reuseCachedInstance);
		}
		
		/// <summary>
		/// Gets extension objects registered in a path.
		/// </summary>
		/// <param name="path">
		/// An extension path.
		/// </param>
		/// <param name="arrayElementType">
		/// Type of the return array elements.
		/// </param>
		/// <param name="reuseCachedInstance">
		/// When set to True, it will return instances created in previous calls.
		/// </param>
		/// <returns>
		/// An array of objects registered in the path.
		/// </returns>
		/// <remarks>
		/// This method can only be used if all nodes in the provided extension path
		/// are of type Mono.Addins.TypeExtensionNode. The returned array is composed
		/// by all objects created by calling the TypeExtensionNode.CreateInstance()
		/// method for each node (or TypeExtensionNode.GetInstance() if
		/// reuseCachedInstance is set to true).
		/// 
		/// An InvalidOperationException exception is thrown if one of the found
		/// objects is not a subclass of the provided type.
		/// </remarks>
		public object[] GetExtensionObjects (string path, Type arrayElementType, bool reuseCachedInstance)
		{
			ExtensionNode node = GetExtensionNode (path);
			if (node == null)
				throw new InvalidOperationException ("Extension node not found in path: " + path);
			return node.GetChildObjects (arrayElementType, reuseCachedInstance);
		}
		
		/// <summary>
		/// Register a listener of extension node changes.
		/// </summary>
		/// <param name="path">
		/// Path of the node.
		/// </param>
		/// <param name="handler">
		/// A handler method.
		/// </param>
		/// <remarks>
		/// Hosts can call this method to be subscribed to an extension change
		/// event for a specific path. The event will be fired once for every
		/// individual node change. The event arguments include the change type
		/// (Add or Remove) and the extension node added or removed.
		/// 
		/// NOTE: The handler will be called for all nodes existing in the path at the moment of registration.
		/// </remarks>
		public void AddExtensionNodeHandler (string path, ExtensionNodeEventHandler handler)
		{
			ExtensionNode node = GetExtensionNode (path);
			if (node == null)
				throw new InvalidOperationException ("Extension node not found in path: " + path);
			node.ExtensionNodeChanged += handler;
		}
		
		/// <summary>
		/// Unregister a listener of extension node changes.
		/// </summary>
		/// <param name="path">
		/// Path of the node.
		/// </param>
		/// <param name="handler">
		/// A handler method.
		/// </param>
		/// <remarks>
		/// This method unregisters a delegate from the node change event of a path.
		/// </remarks>
		public void RemoveExtensionNodeHandler (string path, ExtensionNodeEventHandler handler)
		{
			ExtensionNode node = GetExtensionNode (path);
			if (node == null)
				throw new InvalidOperationException ("Extension node not found in path: " + path);
			node.ExtensionNodeChanged -= handler;
		}
		
		/// <summary>
		/// Register a listener of extension node changes.
		/// </summary>
		/// <param name="instanceType">
		/// Type defining the extension point
		/// </param>
		/// <param name="handler">
		/// A handler method.
		/// </param>
		/// <remarks>
		/// Hosts can call this method to be subscribed to an extension change
		/// event for a specific type extension point. The event will be fired once for every
		/// individual node change. The event arguments include the change type
		/// (Add or Remove) and the extension node added or removed.
		/// 
		/// NOTE: The handler will be called for all nodes existing in the path at the moment of registration.
		/// </remarks>
		public void AddExtensionNodeHandler (Type instanceType, ExtensionNodeEventHandler handler)
		{
			string path = AddinEngine.GetAutoTypeExtensionPoint (instanceType);
			if (path == null)
				throw new InvalidOperationException ("Type '" + instanceType + "' not bound to an extension point.");
			AddExtensionNodeHandler (path, handler);
		}
		
		/// <summary>
		/// Unregister a listener of extension node changes.
		/// </summary>
		/// <param name="instanceType">
		/// Type defining the extension point
		/// </param>
		/// <param name="handler">
		/// A handler method.
		/// </param>
		public void RemoveExtensionNodeHandler (Type instanceType, ExtensionNodeEventHandler handler)
		{
			string path = AddinEngine.GetAutoTypeExtensionPoint (instanceType);
			if (path == null)
				throw new InvalidOperationException ("Type '" + instanceType + "' not bound to an extension point.");
			RemoveExtensionNodeHandler (path, handler);
		}

		internal ExtensionContextTransaction BeginTransaction ()
		{
			return new ExtensionContextTransaction (this);
		}
		
		void OnConditionChanged (object s, EventArgs a)
		{
			ConditionType cond = (ConditionType) s;
			NotifyConditionChanged (cond);
		}

		internal void NotifyConditionChanged (ConditionType cond)
		{
			HashSet<TreeNode> parentsToNotify = null;

			if (conditionTypes.TryGetValue (cond.Id, out var info) && info.BoundConditions != null) {
				parentsToNotify = new HashSet<TreeNode> ();
				foreach (BaseCondition c in info.BoundConditions) {
					if (conditionsToNodes.TryGetValue(c, out var nodeList)) {
						parentsToNotify.UnionWith (nodeList.Select (node => node.Parent));
					}
				}
			}

			if (parentsToNotify != null) {
				foreach (TreeNode node in parentsToNotify) {
					if (node.NotifyChildrenChanged ())
						NotifyExtensionsChanged (new ExtensionEventArgs (node.GetPath ()));
				}
			}

			foreach (var ctx in GetActiveChildContexes ())
				ctx.NotifyConditionChanged (cond);
		}

		IEnumerable<ExtensionContext> GetActiveChildContexes ()
		{
			// Collect a list of child contexts that are still referenced
			if (childContexts.Length > 0) {
				CleanDisposedChildContexts ();
				return childContexts.Select (t => (ExtensionContext)t.Target).Where (t => t != null);
			} else
				return Array.Empty<ExtensionContext> ();
		}


		internal void NotifyExtensionsChanged (ExtensionEventArgs args)
		{
			if (ExtensionChanged != null)
				ExtensionChanged (this, args);
		}
		
		internal void NotifyAddinLoaded (RuntimeAddin ad)
		{
			tree.NotifyAddinLoaded (ad, true);

			foreach (var ctx in GetActiveChildContexes())
				ctx.NotifyAddinLoaded (ad);
		}
		
		internal void CreateExtensionPoint (ExtensionPoint ep)
		{
			TreeNode node = tree.GetNode (ep.Path, true);
			if (node.ExtensionPoint == null) {
				node.ExtensionPoint = ep;
				node.ExtensionNodeSet = ep.NodeSet;
			}
		}
		
		internal void ActivateAddinExtensions (ExtensionContextTransaction transaction, string id)
		{
			// Looks for loaded extension points which are extended by the provided
			// add-in, and adds the new nodes
			
			Addin addin = AddinEngine.Registry.GetAddin (id);
			if (addin == null) {
				AddinEngine.ReportError ("Required add-in not found", id, null, false);
				return;
			}
			// Take note that this add-in has been enabled at run-time
			// Needed because loaded add-in descriptions may not include this add-in. 
			RegisterRuntimeEnabledAddin (transaction, id);
				
			// Look for loaded extension points
			Hashtable eps = new Hashtable ();
			foreach (ModuleDescription mod in addin.Description.AllModules) {
				foreach (Extension ext in mod.Extensions) {
					transaction.NotifyExtensionsChangedEvent (ext.Path);
					ExtensionPoint ep = tree.FindLoadedExtensionPoint (ext.Path);
					if (ep != null && !eps.Contains (ep))
						eps.Add (ep, ep);
				}
			}
				
			// Add the new nodes
			foreach (ExtensionPoint ep in eps.Keys) {
				ExtensionLoadData data = GetAddinExtensions (id, ep);
				if (data != null) {
					foreach (Extension ext in data.Extensions) {
						TreeNode node = GetNode (ext.Path);
						if (node != null && node.ExtensionNodeSet != null) {
							if (node.ChildrenLoaded) {
								var builder = TreeNodeBuilder.FromNode (node);
								LoadModuleExtensionNodes (transaction, builder, ext, data.AddinId);
								builder.Build (transaction);
							}
						}
						else
							AddinEngine.ReportError ("Extension node not found or not extensible: " + ext.Path, id, null, false);
					}
				}
			}
				
			// Do the same in child contexts

			foreach (var ctx in GetActiveChildContexes ())
				ctx.ActivateAddinExtensions (transaction, id);
		}

		internal void RemoveAddinExtensions (ExtensionContextTransaction transaction, string id)
		{
			// Registers this add-in as disabled, so from now on extension from this
			// add-in will be ignored
			RegisterRuntimeDisabledAddin (transaction, id);

			// This method removes all extension nodes added by the add-in
			// Get all nodes created by the addin
			List<TreeNode> list = new List<TreeNode> ();
			tree.FindAddinNodes (id, list);

			// Remove each node and notify the change
			foreach (TreeNode node in list) {
				node.NotifyAddinUnloaded ();
				node.Remove (transaction);
			}

			// Notify global extension point changes.
			// The event is called for all extensions, even for those not loaded. This is for coherence,
			// although that something that it doesn't make much sense to do (subscribing the ExtensionChanged
			// event without first getting the list of nodes that may change).

			// We get the runtime add-in because the add-in may already have been deleted from the registry
			RuntimeAddin addin = AddinEngine.GetAddin (id);
			if (addin != null) {
				var paths = new List<string> ();
				// Using addin.Module.ParentAddinDescription here because addin.Addin.Description may not
				// have a valid reference (the description is lazy loaded and may already have been removed from the registry)
				foreach (ModuleDescription mod in addin.Module.ParentAddinDescription.AllModules) {
					foreach (Extension ext in mod.Extensions) {
						if (!paths.Contains (ext.Path))
							paths.Add (ext.Path);
					}
				}
				foreach (string path in paths)
					NotifyExtensionsChanged (new ExtensionEventArgs (path));
			}
		}
		
		void RegisterRuntimeDisabledAddin (ExtensionContextTransaction transaction, string addinId)
		{
			if (!runTimeDisabledAddins.Contains (addinId))
				runTimeDisabledAddins = runTimeDisabledAddins.Add (addinId);
			
			runTimeEnabledAddins = runTimeEnabledAddins.Remove (addinId);
		}
		
		void RegisterRuntimeEnabledAddin (ExtensionContextTransaction transaction, string addinId)
		{
			if (!runTimeEnabledAddins.Contains (addinId))
				runTimeEnabledAddins = runTimeEnabledAddins.Add (addinId);

			runTimeDisabledAddins = runTimeDisabledAddins.Remove (addinId);
		}
		
		internal List<string> GetAddinsForPath (List<string> col)
		{
			List<string> newlist = null;

			// Always consider add-ins which have been enabled at runtime since
			// they may contain extension for this path.
			// Ignore addins disabled at run-time.

			var enabledAddins = runTimeEnabledAddins;

			if (enabledAddins != null && enabledAddins.Length > 0) {
				newlist = new List<string> ();
				newlist.AddRange (col);
				foreach (string s in enabledAddins)
					if (!newlist.Contains (s))
						newlist.Add (s);
			}

			var disabledAddins = runTimeDisabledAddins;
			if (disabledAddins != null && disabledAddins.Length > 0) {
				if (newlist == null) {
					newlist = new List<string> ();
					newlist.AddRange (col);
				}
				foreach (string s in disabledAddins)
					newlist.Remove (s);
			}
			
			return newlist != null ? newlist : col;
		}
		
		// Load the extension nodes at the specified path. If the path
		// contains extension nodes implemented in an add-in which is
		// not loaded, the add-in will be automatically loaded
		
		internal void LoadExtensions (ExtensionContextTransaction transaction, ExtensionPoint ep, TreeNodeBuilder node)
		{
			// Collect extensions to be loaded from add-ins. Before loading the extensions,
			// they must be sorted, that's why loading is split in two steps (collecting + loading).

			var addins = GetAddinsForPath (ep.Addins);
			var loadData = new List<ExtensionLoadData> (addins.Count);

			foreach (string addin in addins) {
				ExtensionLoadData ed = GetAddinExtensions (addin, ep);
				if (ed != null) {
					// Insert the addin data taking into account dependencies.
					// An add-in must be processed after all its dependencies.
					bool added = false;
					for (int n=0; n<loadData.Count; n++) {
						ExtensionLoadData other = loadData [n];
						if (AddinEngine.Registry.AddinDependsOn (other.AddinName, ed.AddinName)) {
							loadData.Insert (n, ed);
							added = true;
							break;
						}
					}
					if (!added)
						loadData.Add (ed);
				}
			}
				
			// Now load the extensions
				
			foreach (ExtensionLoadData data in loadData) {
				foreach (Extension ext in data.Extensions) {
					var cnode = node.GetNode (ext.Path);
					if (cnode != null)
						LoadModuleExtensionNodes (transaction, cnode, ext, data.AddinId);
					else
						AddinEngine.ReportError ("Extension node not found or not extensible: " + ext.Path, data.AddinId, null, false);
				}
			}
		}
		
		ExtensionLoadData GetAddinExtensions (string id, ExtensionPoint ep)
		{
			Addin pinfo = null;

			// Root add-ins are not returned by GetInstalledAddin.
			RuntimeAddin addin = AddinEngine.GetAddin (id);
			if (addin != null)
				pinfo = addin.Addin;
			else
				pinfo = AddinEngine.Registry.GetAddin (id);
			
			if (pinfo == null) {
				AddinEngine.ReportError ("Required add-in not found", id, null, false);
				return null;
			}
			if (!pinfo.Enabled || pinfo.Version != Addin.GetIdVersion (id))
				return null;

			// Loads extensions defined in each module
			ExtensionLoadData data = null;
			AddinDescription conf = pinfo.Description;
			GetAddinExtensions (conf.MainModule, id, ep, ref data);
			
			foreach (ModuleDescription module in conf.OptionalModules) {
				if (CheckOptionalAddinDependencies (conf, module))
					GetAddinExtensions (module, id, ep, ref data);
			}
			if (data != null)
				data.Extensions.Sort ();

			return data;
		}
		
		void GetAddinExtensions (ModuleDescription module, string addinId, ExtensionPoint ep, ref ExtensionLoadData data)
		{
			string basePath = ep.Path + "/";

			string addinName = Addin.GetIdName (addinId);
			foreach (Extension extension in module.Extensions) {
				if (extension.Path == ep.Path || extension.Path.StartsWith (basePath, StringComparison.Ordinal)) {
					if (data == null) {
						data = new ExtensionLoadData ();
						data.AddinId = addinId;
						data.AddinName = addinName;
						data.Extensions = new List<Extension> ();
					}
					data.Extensions.Add (extension);
				}
			}
		}
		
		void LoadModuleExtensionNodes (ExtensionContextTransaction transaction, TreeNodeBuilder node, Extension extension, string addinId)
		{
			// Now load the extensions
			var addedNodes = new List<TreeNode> ();
			tree.LoadExtension (transaction, node, addinId, extension, addedNodes);
			
			RuntimeAddin ad = AddinEngine.GetAddin (addinId);
			if (ad != null) {
				foreach (TreeNode nod in addedNodes) {
					// Don't call OnAddinLoaded here. Do it when the entire extension point has been loaded.
					if (nod.HasExtensionNode)
						transaction.ReportLoadedNode (nod);
				}
			}
		}
		
		bool CheckOptionalAddinDependencies (AddinDescription conf, ModuleDescription module)
		{
			foreach (Dependency dep in module.Dependencies) {
				AddinDependency pdep = dep as AddinDependency;
				if (pdep != null) {
					Addin pinfo = AddinEngine.Registry.GetAddin (Addin.GetFullId (conf.Namespace, pdep.AddinId, pdep.Version));
					if (pinfo == null || !pinfo.Enabled)
						return false;
				}
			}
			return true;
		}

		
		TreeNode GetNode (string path)
		{
			TreeNode node = tree.GetNode (path);
			if (node != null || parentContext == null)
				return node;

			TreeNode supNode = parentContext.tree.GetNode (path);
			if (supNode == null)
				return null;

			// Node not found and the context has a parent context which has the node

			if (path.StartsWith ("/"))
				path = path.Substring (1);

			string[] parts = path.Split ('/');
			TreeNode srcNode = parentContext.tree;
			TreeNode dstNode = tree;

			foreach (string part in parts) {

				// Look for the node in the source tree (from parent context)

				srcNode = srcNode.GetChildNode (part);
				if (srcNode == null)
					return null;

				// Now get the node in the target tree

				var dstNodeChild = dstNode.GetChildNode (part);
				if (dstNodeChild != null) {
					dstNode = dstNodeChild;
				}
				else {
					using var transaction = BeginTransaction ();

					// Check again just in case the node was created while taking the transaction
					dstNodeChild = dstNode.GetChildNode (part);
					if (dstNodeChild != null)
						dstNode = dstNodeChild;
					else {

						// Create if not found
						// Copy extension data from the parent context node

						TreeNode newNode = srcNode.Clone (AddinEngine);

						if (newNode.Condition != null)
							transaction.RegisterNodeCondition (newNode, newNode.Condition);

						// Don't rise extension change events since we are just building the tree, not modifying it
						transaction.DisableEvents = true;

						dstNode.AddChildNode (transaction, newNode);
						dstNode = newNode;
					}
				}
			}
			
			return dstNode;
		}
		
		internal bool FindExtensionPathByType (IProgressStatus monitor, Type type, string nodeName, out string path, out string pathNodeName)
		{
			return tree.FindExtensionPathByType (monitor, type, nodeName, out path, out pathNodeName);
		}
	}
	
	class ConditionInfo
	{
		public object CondType;
		public List<BaseCondition> BoundConditions;
	}

	
	/// <summary>
	/// Delegate to be used in extension point subscriptions
	/// </summary>
	public delegate void ExtensionEventHandler (object sender, ExtensionEventArgs args);
	
	/// <summary>
	/// Delegate to be used in extension point subscriptions
	/// </summary>
	public delegate void ExtensionNodeEventHandler (object sender, ExtensionNodeEventArgs args);
	
	/// <summary>
	/// Arguments for extension events.
	/// </summary>
	public class ExtensionEventArgs: EventArgs
	{
		string path;
		
		internal ExtensionEventArgs ()
		{
		}
		
		/// <summary>
		/// Creates a new instance.
		/// </summary>
		/// <param name="path">
		/// Path of the extension node that has changed.
		/// </param>
		public ExtensionEventArgs (string path)
		{
			this.path = path;
		}
		
		/// <summary>
		/// Path of the extension node that has changed.
		/// </summary>
		public virtual string Path {
			get { return path; }
		}
		
		/// <summary>
		/// Checks if a path has changed.
		/// </summary>
		/// <param name="pathToCheck">
		/// An extension path.
		/// </param>
		/// <returns>
		/// 'true' if the path is affected by the extension change event.
		/// </returns>
		/// <remarks>
		/// Checks if the specified path or any of its children paths is affected by the extension change event.
		/// </remarks>
		public bool PathChanged (string pathToCheck)
		{
			if (pathToCheck.EndsWith ("/"))
				return path.StartsWith (pathToCheck);
			else
				return path.StartsWith (pathToCheck) && (pathToCheck.Length == path.Length || path [pathToCheck.Length] == '/');
		}
	}
	
	/// <summary>
	/// Arguments for extension node events.
	/// </summary>
	public class ExtensionNodeEventArgs: ExtensionEventArgs
	{
		ExtensionNode node;
		ExtensionChange change;
		
		/// <summary>
		/// Creates a new instance
		/// </summary>
		/// <param name="change">
		/// Type of change.
		/// </param>
		/// <param name="node">
		/// Node that has been added or removed.
		/// </param>
		public ExtensionNodeEventArgs (ExtensionChange change, ExtensionNode node)
		{
			this.node = node;
			this.change = change;
		}
		
		/// <summary>
		/// Path of the extension that changed.
		/// </summary>
		public override string Path {
			get { return node.Path; }
		}
		
		/// <summary>
		/// Type of change.
		/// </summary>
		public ExtensionChange Change {
			get { return change; }
		}
		
		/// <summary>
		/// Node that has been added or removed.
		/// </summary>
		public ExtensionNode ExtensionNode {
			get { return node; }
		}
		
		/// <summary>
		/// Extension object that has been added or removed.
		/// </summary>
		public object ExtensionObject {
			get {
				InstanceExtensionNode tnode = node as InstanceExtensionNode;
				if (tnode == null)
					throw new InvalidOperationException ("Node is not an InstanceExtensionNode");
				return tnode.GetInstance (); 
			}
		}
	}
	
	/// <summary>
	/// Type of change in an extension change event.
	/// </summary>
	public enum ExtensionChange
	{
		/// <summary>
		/// An extension node has been added.
		/// </summary>
		Add,
		
		/// <summary>
		/// An extension node has been removed.
		/// </summary>
		Remove
	}

	
	internal class ExtensionLoadData
	{
		public string AddinId;
		public string AddinName;
		public List<Extension> Extensions;
	}

	class ConditionTypeData
	{
		public string TypeName { get; set; }
		public RuntimeAddin Addin { get; set; }
	}
}
