﻿// Copyright 2017, Google Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore.V1Beta1;
using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Google.Cloud.Firestore.V1Beta1.StructuredQuery.Types;
using FieldOp = Google.Cloud.Firestore.V1Beta1.StructuredQuery.Types.FieldFilter.Types.Operator;
using UnaryOp = Google.Cloud.Firestore.V1Beta1.StructuredQuery.Types.UnaryFilter.Types.Operator;

namespace Google.Cloud.Firestore
{
    /// <summary>
    /// A query against a collection.
    /// </summary>
    /// <remarks>
    /// <see cref="CollectionReference"/> derives from this class as a "return-all" query against the
    /// collection it refers to.
    /// </remarks>
    public class Query : IEquatable<Query>
    {
        // These are all read-only, but may be mutable. They should never be mutated;
        // multiple Query objects may share the same internal references.
        // Any additional fields should be included in equality/hash code checks.
        internal CollectionReference Collection { get; }
        private readonly int _offset;
        private readonly int? _limit;
        private readonly IReadOnlyList<InternalOrdering> _orderings; // Never null
        private readonly IReadOnlyList<InternalFilter> _filters; // May be null
        private readonly IReadOnlyList<FieldPath> _projections; // May be null
        private readonly Cursor _startAt;
        private readonly Cursor _endAt;

        /// <summary>
        /// The database this query will search over.
        /// </summary>
        public virtual FirestoreDb Database => Collection.Database;

        // Parent path of this query
        internal string ParentPath => Collection.Parent?.Path ?? Database.DocumentsPath;

        // This would be protected, but that would allow subclasses from other assemblies. The intention is that the only concrete
        // subclass of Query is CollectionReference. If "private protected" ever ends up in C#, this constructor can be changed.
        internal Query()
        {
            Collection = this as CollectionReference;
            GaxPreconditions.CheckState(Collection != null, "Internal Query constructor should only be used from CollectionReference");
            _orderings = new List<InternalOrdering>();
        }

        // Constructor used for all the fluent interface methods. This contains all the fields, which are copied verbatim with
        // no further cloning: it is the responsibility of each method to ensure it creates a clone for any new data.
        private Query(
            CollectionReference collection, int offset, int? limit,
            IReadOnlyList<InternalOrdering> orderings, IReadOnlyList<InternalFilter> filters, IReadOnlyList<FieldPath> projections,
            Cursor startAt, Cursor endAt)
        {
            Collection = collection;
            _offset = offset;
            _limit = limit;
            _orderings = orderings;
            _filters = filters;
            _projections = projections;
            _startAt = startAt;
            _endAt = endAt;
        }

        internal StructuredQuery ToStructuredQuery() =>
            new StructuredQuery
            {
                From = { new CollectionSelector { CollectionId = Collection.Id } },
                Limit = _limit,
                Offset = _offset,
                OrderBy = { _orderings.Select(o => o.ToProto()) },
                EndAt = _endAt,
                Select = _projections == null ? null : new Projection { Fields = { _projections.Select(fp => fp.ToFieldReference()) } },
                StartAt = _startAt,
                Where = _filters == null ? null
                    : _filters.Count == 1 ? _filters[0].ToProto()
                    : new Filter { CompositeFilter = new CompositeFilter { Op = CompositeFilter.Types.Operator.And, Filters = { _filters.Select(f => f.ToProto()) } } }
            };

        /// <summary>
        /// Specifies the field paths to return in the results.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously-specified projections in the query.
        /// </remarks>
        /// <param name="fieldPaths">The dot-separated field paths to select. Must not be null or empty, or contain null or empty
        /// elements.</param>
        /// <returns>A new query based on the current one, but with the specified projection applied.</returns>
        public Query Select(params string[] fieldPaths)
        {
            GaxPreconditions.CheckNotNull(fieldPaths, nameof(fieldPaths));
            // Note: if a null or empty element is passed in, we'll currently throw an exception from FieldPath.FromDotSeparatedString.
            // Not sure whether it's worth reimplementing the checks - and we wouldn't want to do *all* validation...
            FieldPath[] convertedFieldPaths = fieldPaths.Select(FieldPath.FromDotSeparatedString).ToArray();
            return Select(convertedFieldPaths);
        }

        /// <summary>
        /// Specifies the field paths to return in the results.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously-specified projections in the query.
        /// </remarks>
        /// <param name="fieldPaths">The field paths to select. Must not be null or contain null elements.
        /// If this is empty, the document ID is implicitly selected.</param>
        /// <returns>A new query based on the current one, but with the specified projection applied.</returns>
        public Query Select(params FieldPath[] fieldPaths)
        {
            GaxPreconditions.CheckNotNull(fieldPaths, nameof(fieldPaths));
            GaxPreconditions.CheckArgument(!fieldPaths.Contains(null), nameof(fieldPaths), "Field paths must not contain a null element");
            if (fieldPaths.Length == 0)
            {
                fieldPaths = new[] { FieldPath.DocumentId };
            }
            return new Query(Collection, _offset, _limit, _orderings, _filters, new List<FieldPath>(fieldPaths), _startAt, _endAt);
        }

        // TODO: Choices...
        // - Use an enum instead of strings?
        // - Rename the "op" parameter? (operator is a keyword, but we could make it @operator maybe)
        // - Rename "Where" to "AddFilter"?
        // - Reimplement as individual methods, two per filter operator (accepting string or FieldPath),
        //   e.g. WhereEqual, WhereLess than etc

        /// <summary>
        /// Add a filter for the given field path.
        /// </summary>
        /// <remarks>
        /// This call adds additional filters to any previously-specified ones.
        /// </remarks>
        /// <param name="fieldPath">The dot-separated field path to filter on. Must not be null or empty.</param>
        /// <param name="op">The filter operator. Must not be null.</param>
        /// <param name="value">The value to compare in the filter.</param>
        /// <returns>A new query based on the current one, but with the additional specified filter applied.</returns>
        public Query Where(string fieldPath, QueryOperator op, object value)
        {
            GaxPreconditions.CheckNotNullOrEmpty(fieldPath, nameof(fieldPath));
            return Where(FieldPath.FromDotSeparatedString(fieldPath), op, value);
        }

        /// <summary>
        /// Add a filter for the given field path.
        /// </summary>
        /// <remarks>
        /// This call adds additional filters to any previously-specified ones.
        /// </remarks>
        /// <param name="fieldPath">The field path to filter on. Must not be null.</param>
        /// <param name="op">The filter operator.</param>
        /// <param name="value">The value to compare in the filter. Must not be a sentinel value.</param>
        /// <returns>A new query based on the current one, but with the additional specified filter applied.</returns>
        public Query Where(FieldPath fieldPath, QueryOperator op, object value)
        {
            InternalFilter filter = InternalFilter.Create(fieldPath, op, value);
            var newFilters = _filters == null ? new List<InternalFilter>() : new List<InternalFilter>(_filters);
            newFilters.Add(filter);
            return new Query(Collection, _offset, _limit, _orderings, newFilters, _projections, _startAt, _endAt);
        }

        /// <summary>
        /// Adds an additional ascending ordering by the specified path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unlike LINQ's OrderBy method, this call adds additional subordinate orderings to any
        /// additionally specified. So <c>query.OrderBy("foo").OrderBy("bar")</c> is similar
        /// to a LINQ <c>query.OrderBy(x => x.Foo).ThenBy(x => x.Bar)</c>.
        /// </para>
        /// <para>
        /// This method cannot be called after a start/end cursor has been specified with
        /// <see cref="StartAt(object[])"/>, <see cref="StartAfter(object[])"/>, <see cref="EndAt(object[])"/> or <see cref="EndBefore(object[])"/>.
        /// </para>
        /// </remarks>
        /// <param name="fieldPath">The dot-separated field path to order by. Must not be null or empty.</param>
        /// <returns>A new query based on the current one, but with the additional specified ordering applied.</returns>
        public Query OrderBy(string fieldPath) => OrderBy(fieldPath, Direction.Ascending);

        /// <summary>
        /// Adds an additional descending ordering by the specified path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unlike LINQ's OrderBy method, this call adds additional subordinate orderings to any
        /// additionally specified. So <c>query.OrderBy("foo").OrderByDescending("bar")</c> is similar
        /// to a LINQ <c>query.OrderBy(x => x.Foo).ThenByDescending(x => x.Bar)</c>.
        /// </para>
        /// <para>
        /// This method cannot be called after a start/end cursor has been specified with
        /// <see cref="StartAt(object[])"/>, <see cref="StartAfter(object[])"/>, <see cref="EndAt(object[])"/> or <see cref="EndBefore(object[])"/>.
        /// </para>
        /// </remarks>
        /// <param name="fieldPath">The dot-separated field path to order by. Must not be null or empty.</param>
        /// <returns>A new query based on the current one, but with the additional specified ordering applied.</returns>
        public Query OrderByDescending(string fieldPath) => OrderBy(fieldPath, Direction.Descending);

        /// <summary>
        /// Adds an additional ascending ordering by the specified path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unlike LINQ's OrderBy method, this call adds additional subordinate orderings to any
        /// additionally specified. So <c>query.OrderBy("foo").OrderBy("bar")</c> is similar
        /// to a LINQ <c>query.OrderBy(x => x.Foo).ThenBy(x => x.Bar)</c>.
        /// </para>
        /// <para>
        /// This method cannot be called after a start/end cursor has been specified with
        /// <see cref="StartAt(object[])"/>, <see cref="StartAfter(object[])"/>, <see cref="EndAt(object[])"/> or <see cref="EndBefore(object[])"/>.
        /// </para>
        /// </remarks>
        /// <param name="fieldPath">The field path to order by. Must not be null.</param>
        /// <returns>A new query based on the current one, but with the additional specified ordering applied.</returns>
        public Query OrderBy(FieldPath fieldPath) => OrderBy(GaxPreconditions.CheckNotNull(fieldPath, nameof(fieldPath)), Direction.Ascending);

        /// <summary>
        /// Adds an additional descending ordering by the specified path.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unlike LINQ's OrderBy method, this call adds additional subordinate orderings to any
        /// additionally specified. So <c>query.OrderBy("foo").OrderByDescending("bar")</c> is similar
        /// to a LINQ <c>query.OrderBy(x => x.Foo).ThenByDescending(x => x.Bar)</c>.
        /// </para>
        /// <para>
        /// This method cannot be called after a start/end cursor has been specified with
        /// <see cref="StartAt(object[])"/>, <see cref="StartAfter(object[])"/>, <see cref="EndAt(object[])"/> or <see cref="EndBefore(object[])"/>.
        /// </para>
        /// </remarks>
        /// <param name="fieldPath">The field path to order by. Must not be null.</param>
        /// <returns>A new query based on the current one, but with the additional specified ordering applied.</returns>
        public Query OrderByDescending(FieldPath fieldPath) => OrderBy(GaxPreconditions.CheckNotNull(fieldPath, nameof(fieldPath)), Direction.Descending);

        private Query OrderBy(string fieldPath, Direction direction)
        {
            GaxPreconditions.CheckNotNullOrEmpty(fieldPath, nameof(fieldPath));
            return OrderBy(FieldPath.FromDotSeparatedString(fieldPath), direction);
        }

        private Query OrderBy(FieldPath fieldPath, Direction direction)
        {
            GaxPreconditions.CheckState(_startAt == null && _endAt == null,
                "All orderings must be specified before StartAt, StartAfter, EndBefore or EndAt are called.");
            var newOrderings = new List<InternalOrdering>(_orderings) { new InternalOrdering(fieldPath, direction) };
            return new Query(Collection, _offset, _limit, newOrderings, _filters, _projections, _startAt, _endAt);
        }

        /// <summary>
        /// Specifies the maximum number of results to return.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously-specified limit in the query.
        /// </remarks>
        /// <param name="limit">The maximum number of results to return. Must be greater than or equal to 0.</param>
        /// <returns>A new query based on the current one, but with the specified limit applied.</returns>
        public Query Limit(int limit)
        {
            GaxPreconditions.CheckArgumentRange(limit, nameof(limit), 0, int.MaxValue);
            return new Query(Collection, _offset, limit, _orderings, _filters, _projections, _startAt, _endAt);
        }
        
        /// <summary>
        /// Specifies a number of results to skip.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously-specified offset in the query.
        /// </remarks>
        /// <param name="offset">The number of results to skip. Must be greater than or equal to 0.</param>
        /// <returns>A new query based on the current one, but with the specified offset applied.</returns>
        public Query Offset(int offset)
        {
            GaxPreconditions.CheckArgumentRange(offset, nameof(offset), 0, int.MaxValue);
            return new Query(Collection, offset, _limit, _orderings, _filters, _projections, _startAt, _endAt);
        }

        /// <summary>
        /// Creates and returns a new query that starts at the provided fields relative to the order of the
        /// query. The order of the field values must match the order of the order-by clauses of the query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified start position in the query.
        /// </remarks>
        /// <param name="fieldValues">The field values. Must not be null or empty, or have more values than query has orderings.</param>
        /// <returns>A new query based on the current one, but with the specified start position.</returns>
        public Query StartAt(params object[] fieldValues) => StartAt(fieldValues, true);

        /// <summary>
        /// Creates and returns a new query that starts after the provided fields relative to the order of the
        /// query. The order of the field values must match the order of the order-by clauses of the query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified start position in the query.
        /// </remarks>
        /// <param name="fieldValues">The field values. Must not be null or empty, or have more values than query has orderings.</param>
        /// <returns>A new query based on the current one, but with the specified start position.</returns>
        public Query StartAfter(params object[] fieldValues) => StartAt(fieldValues, false);

        /// <summary>
        /// Creates and returns a new query that ends before the provided fields relative to the order of the
        /// query. The order of the field values must match the order of the order-by clauses of the query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified end position in the query.
        /// </remarks>
        /// <param name="fieldValues">The field values. Must not be null or empty, or have more values than query has orderings.</param>
        /// <returns>A new query based on the current one, but with the specified end position.</returns>
        public Query EndBefore(params object[] fieldValues) => EndAt(fieldValues, true);

        /// <summary>
        /// Creates and returns a new query that ends at the provided fields relative to the order of the
        /// query. The order of the field values must match the order of the order-by clauses of the query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified end position in the query.
        /// </remarks>
        /// <param name="fieldValues">The field values. Must not be null or empty, or have more values than query has orderings.</param>
        /// <returns>A new query based on the current one, but with the specified end position.</returns>
        public Query EndAt(params object[] fieldValues) => EndAt(fieldValues, false);        

        /// <summary>
        /// Creates and returns a new query that starts at the document snapshot provided fields relative to the order of the
        /// query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified start position in the query.
        /// </remarks>
        /// <param name="snapshot">The snapshot of the document to start at. Must not be null.</param>
        /// <returns>A new query based on the current one, but with the specified start position.</returns>
        public Query StartAt(DocumentSnapshot snapshot) => StartAtSnapshot(snapshot, true);

        /// <summary>
        /// Creates and returns a new query that starts after the document snapshot provided fields relative to the order of the
        /// query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified start position in the query.
        /// </remarks>
        /// <param name="snapshot">The snapshot of the document to start after. Must not be null.</param>
        /// <returns>A new query based on the current one, but with the specified start position.</returns>
        public Query StartAfter(DocumentSnapshot snapshot) => StartAtSnapshot(snapshot, false);

        /// <summary>
        /// Creates and returns a new query that ends before the document snapshot provided fields relative to the order of the
        /// query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified end position in the query.
        /// </remarks>
        /// <param name="snapshot">The snapshot of the document to end before. Must not be null.</param>
        /// <returns>A new query based on the current one, but with the specified end position.</returns>
        public Query EndBefore(DocumentSnapshot snapshot) => EndAtSnapshot(snapshot, true);

        /// <summary>
        /// Creates and returns a new query that ends at the document snapshot provided fields relative to the order of the
        /// query.
        /// </summary>
        /// <remarks>
        /// This call replaces any previously specified end position in the query.
        /// </remarks>
        /// <param name="snapshot">The snapshot of the document to end at.</param>
        /// <returns>A new query based on the current one, but with the specified end position.</returns>
        public Query EndAt(DocumentSnapshot snapshot) => EndAtSnapshot(snapshot, false);

        /// <summary>
        /// Asynchronously takes a snapshot of all documents matching the query.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token for the operation.</param>
        /// <returns>A snapshot of documents matching the query.</returns>
        public Task<QuerySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => GetSnapshotAsync(null, cancellationToken);

        internal async Task<QuerySnapshot> GetSnapshotAsync(ByteString transactionId, CancellationToken cancellationToken)
        {
            var responses = StreamResponsesAsync(transactionId, cancellationToken);
            Timestamp? readTime = null;
            List<DocumentSnapshot> snapshots = new List<DocumentSnapshot>();
            await responses.ForEachAsync(response =>
            {
                if (response.Document != null)
                {
                    snapshots.Add(DocumentSnapshot.ForDocument(Database, response.Document, Timestamp.FromProto(response.ReadTime)));
                }
                if (readTime == null && response.ReadTime != null)
                {
                    readTime = Timestamp.FromProto(response.ReadTime);
                }
            }, cancellationToken).ConfigureAwait(false);

            GaxPreconditions.CheckState(readTime != null, "The stream returned from RunQuery did not provide a read timestamp.");

            return new QuerySnapshot(this, snapshots.AsReadOnly(), readTime.Value);
        }

        /// <summary>
        /// Returns an asynchronous sequence of snapshots matching the query.
        /// </summary>
        /// <remarks>
        /// Each time you iterate over the sequence, a new query will be performed.
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token for the operation.</param>
        /// <returns>An asynchronous sequence of document snapshots matching the query.</returns>
        public IAsyncEnumerable<DocumentSnapshot> StreamAsync(CancellationToken cancellationToken = default) => StreamAsync(null, cancellationToken);

        internal IAsyncEnumerable<DocumentSnapshot> StreamAsync(ByteString transactionId, CancellationToken cancellationToken) =>
             StreamResponsesAsync(transactionId, cancellationToken)
                .Where(resp => resp.Document != null)
                .Select(resp => DocumentSnapshot.ForDocument(Database, resp.Document, Timestamp.FromProto(resp.ReadTime)));

        private IAsyncEnumerable<RunQueryResponse> StreamResponsesAsync(ByteString transactionId, CancellationToken cancellationToken)
        {
            var request = new RunQueryRequest { StructuredQuery = ToStructuredQuery(), Parent = ParentPath };
            if (transactionId != null)
            {
                request.Transaction = transactionId;
            }
            var settings = CallSettings.FromCancellationToken(cancellationToken);
            return AsyncEnumerable.CreateEnumerable(() => Database.Client.RunQuery(request, settings).ResponseStream);
        }

        // Helper methods for cursor-related functionality

        internal Query StartAt(object[] fieldValues, bool before) =>
            new Query(Collection, _offset, _limit, _orderings, _filters, _projections, CreateCursor(fieldValues, before), _endAt);

        internal Query EndAt(object[] fieldValues, bool before) =>
            new Query(Collection, _offset, _limit, _orderings, _filters, _projections, _startAt, CreateCursor(fieldValues, before));

        private Cursor CreateCursor(object[] fieldValues, bool before)
        {
            GaxPreconditions.CheckNotNull(fieldValues, nameof(fieldValues));
            GaxPreconditions.CheckArgument(fieldValues.Length != 0, nameof(fieldValues), "Cannot specify an empty set of values for a start/end query cursor.");
            GaxPreconditions.CheckArgument(
                fieldValues.Length <= _orderings.Count,
                nameof(fieldValues),
                "Too many cursor values specified. The specified values must match the ordering constraints of the query. {0} specified for a query with {1} ordering constraints.",
                fieldValues.Length, _orderings.Count);

            var cursor = new Cursor { Before = before };
            for (int i = 0; i < fieldValues.Length; i++)
            {
                object value = fieldValues[i];
                // The DocumentId field path is handled differently to other fields. We accept a string (relative path) or
                // a DocumentReference (absolute path that must be a descendant of this collection).
                if (Equals(_orderings[i].Field, FieldPath.DocumentId))
                {
                    switch (fieldValues[i])
                    {
                        case string relativePath:
                            // Note: this assumes querying over a single collection at the moment.
                            // Convert to a DocumentReference for the cursor
                            PathUtilities.ValidateId(relativePath, nameof(fieldValues));
                            value = Collection.Document(relativePath);
                            break;
                        case DocumentReference absoluteRef:
                            // Just validate that the given document is a direct child of the parent collection.
                            GaxPreconditions.CheckArgument(absoluteRef.Parent.Equals(Collection), nameof(fieldValues),
                                "A DocumentReference cursor value for a document ID must be a descendant of the collection of the query");
                            break;
                        default:
                            throw new ArgumentException($"A cursor value for a document ID must be a string (relative path) or a DocumentReference", nameof(fieldValues));
                    }
                }
                var convertedValue = ValueSerializer.Serialize(value);
                if (convertedValue.IsDeleteSentinel() || convertedValue.IsServerTimestampSentinel())
                {
                    throw new ArgumentException("Snapshot ordering contained a sentinel value");
                }
                cursor.Values.Add(convertedValue);
            }

            return cursor;
        }

        internal Query StartAtSnapshot(DocumentSnapshot snapshot, bool before)
        {
            var cursor = CreateCursorFromSnapshot(snapshot, before, out var newOrderings);
            return new Query(Collection, _offset, _limit, newOrderings, _filters, _projections, cursor, _endAt);
        }

        internal Query EndAtSnapshot(DocumentSnapshot snapshot, bool before)
        {
            var cursor = CreateCursorFromSnapshot(snapshot, before, out var newOrderings);
            return new Query(Collection, _offset, _limit, newOrderings, _filters, _projections, _startAt, cursor);
        }

        private Cursor CreateCursorFromSnapshot(DocumentSnapshot snapshot, bool before, out IReadOnlyList<InternalOrdering> newOrderings)
        {
            GaxPreconditions.CheckArgument(Equals(snapshot.Reference.Parent, Collection),
                nameof(snapshot), "Snapshot was from incorrect collection");

            GaxPreconditions.CheckNotNull(snapshot, nameof(snapshot));
            var cursor = new Cursor { Before = before };
            bool hasDocumentId = false;

            // We may or may not need to add some orderings; this is communicated through the out parameter.
            newOrderings = _orderings;
            // Only used when we need to add orderings; set newOrderings to this at the same time.
            List<InternalOrdering> modifiedOrderings = null;

            if (_orderings.Count == 0 && _filters != null)
            {
                // If no explicit ordering is specified, use the first inequality to define an implicit order.
                foreach (var filter in _filters)
                {
                    if (!filter.IsEqualityFilter())
                    {
                        modifiedOrderings = new List<InternalOrdering>(newOrderings) { new InternalOrdering(filter.Field, Direction.Ascending) };
                        newOrderings = modifiedOrderings;
                    }
                }
            }
            else
            {
                hasDocumentId = _orderings.Any(order => Equals(order.Field, FieldPath.DocumentId));
            }

            if (!hasDocumentId)
            {
                // Add implicit sorting by name, using the last specified direction.
                Direction lastDirection = _orderings.Count == 0 ? Direction.Ascending : _orderings.Last().Direction;

                // Clone iff this is the first new ordering.
                if (modifiedOrderings == null)
                {
                    modifiedOrderings = new List<InternalOrdering>(newOrderings);
                    newOrderings = modifiedOrderings;
                }
                modifiedOrderings.Add(new InternalOrdering(FieldPath.DocumentId, lastDirection));
            }

            foreach (var ordering in newOrderings)
            {
                var field = ordering.Field;
                var value = Equals(field, FieldPath.DocumentId) ? ValueSerializer.Serialize(snapshot.Reference) : snapshot.ExtractValue(field);
                if (value == null)
                {
                    throw new ArgumentException($"Snapshot does not contain field {field}", nameof(snapshot));
                }
                cursor.Values.Add(ValueSerializer.Serialize(value));
            }
            return cursor;
        }

        /// <inheritdoc />
        public override bool Equals(object obj) => Equals(obj as Query);

        // Note: these methods should be equivalent to producing the proto representations and checking those for
        // equality, but that would be expensive.

        /// <summary>
        /// Compares this query with another for equality. Every aspect of the query must be equal,
        /// including the collection. A plain Query instance is not equal to a CollectionReference instance,
        /// even if they are logically similar: <c>collection.Offset(0).Equals(collection)</c> will return
        /// <c>false</c>, even though 0 is the default offset.
        /// </summary>
        /// <param name="other">The query to compare this one with</param>
        /// <returns><c>true</c> if this query is equal to <paramref name="other"/>; <c>false</c> otherwise.</returns>
        public bool Equals(Query other)
        {
            if (ReferenceEquals(other, this))
            {
                return true;
            }
            if (ReferenceEquals(other, null))
            {
                return false;
            }
            if (GetType() != other.GetType())
            {
                return false;
            }
            return Collection.Equals(other.Collection) &&
                _offset == other._offset &&
                _limit == other._limit &&
                EqualityHelpers.ListsEqual(_orderings, other._orderings) &&
                EqualityHelpers.ListsEqual(_filters, other._filters) &&
                EqualityHelpers.ListsEqual(_projections, other._projections) &&
                Equals(_startAt, other._startAt) &&
                Equals(_endAt, other._endAt);
        }

        /// <inheritdoc />
        public override int GetHashCode() => EqualityHelpers.CombineHashCodes(
            Collection.GetHashCode(),
            _offset,
            _limit ?? -1,
            EqualityHelpers.GetListHashCode(_orderings),
            EqualityHelpers.GetListHashCode(_filters),
            EqualityHelpers.GetListHashCode(_projections),
            _startAt?.GetHashCode() ?? -1,
            _endAt?.GetHashCode() ?? -1);
        
        // Structs representing orderings and filters but using FieldPath instead of FieldReference.
        // This allows us to use fields specified in the ordering/filter in this more convenient form.

        private struct InternalOrdering : IEquatable<InternalOrdering>
        {
            internal FieldPath Field { get; }
            internal Direction Direction { get; }
            internal Order ToProto() => new Order { Direction = Direction, Field = Field.ToFieldReference() };

            public override int GetHashCode() => EqualityHelpers.CombineHashCodes(Field.GetHashCode(), (int) Direction);

            internal InternalOrdering(FieldPath field, Direction direction)
            {
                Field = field;
                Direction = direction;
            }

            public override bool Equals(object obj) => obj is InternalOrdering other ? Equals(other) : false;

            public bool Equals(InternalOrdering other) => Field.Equals(other.Field) && Direction == other.Direction;
        }

        private struct InternalFilter : IEquatable<InternalFilter>
        {
            internal FieldPath Field { get; }
            private readonly int _op;
            private readonly Value _value;

            internal Filter ToProto() =>
                _value == null
                ? new Filter { UnaryFilter = new UnaryFilter { Field = Field.ToFieldReference(), Op = (UnaryOp) _op } }
                : new Filter { FieldFilter = new FieldFilter { Field = Field.ToFieldReference(), Op = (FieldOp) _op, Value = _value } };

            private InternalFilter(FieldPath field, int op, Value value)
            {
                Field = field;
                _op = op;
                _value = value;
            }

            /// <summary>
            /// Checks whether this is an equality operator. Unary filters are always equality operators, and field filters can be.
            /// </summary>
            /// <returns></returns>
            internal bool IsEqualityFilter() => _value == null || _op == (int) FieldOp.Equal;

            internal static InternalFilter Create(FieldPath fieldPath, QueryOperator op, object value)
            {
                GaxPreconditions.CheckNotNull(fieldPath, nameof(fieldPath));
                FieldOp queryOp = GetOperator(op);
                var unaryOperator = GetUnaryOperator(value);
                if (unaryOperator != UnaryOp.Unspecified)
                {
                    if (queryOp == FieldOp.Equal)
                    {
                        return new InternalFilter(fieldPath, (int) unaryOperator, null);
                    }
                    else
                    {
                        throw new ArgumentException(nameof(value), "null and NaN values can only be used with the Equal operator");
                    }
                }
                else
                {
                    var convertedValue = ValueSerializer.Serialize(value);
                    if (convertedValue.IsDeleteSentinel() || convertedValue.IsServerTimestampSentinel())
                    {
                        throw new ArgumentException(nameof(value), "Sentinel values cannot be specified in filters");
                    }
                    return new InternalFilter(fieldPath, (int) queryOp, convertedValue);
                }
            }

            private static FieldOp GetOperator(QueryOperator op)
            {
                switch (op)
                {
                    case QueryOperator.Equal: return FieldOp.Equal;
                    case QueryOperator.LessThan: return FieldOp.LessThan;
                    case QueryOperator.LessThanOrEqual: return FieldOp.LessThanOrEqual;
                    case QueryOperator.GreaterThan: return FieldOp.GreaterThan;
                    case QueryOperator.GreaterThanOrEqual: return FieldOp.GreaterThanOrEqual;
                    default:
                        throw new ArgumentException($"No operator for {op}", nameof(op));
                }
            }

            private static UnaryOp GetUnaryOperator(object value)
            {
                switch (value)
                {
                    case null:
                        return UnaryOp.IsNull;
                    case double d when double.IsNaN(d):
                    case float f when float.IsNaN(f):
                        return UnaryOp.IsNan;
                    default:
                        return UnaryOp.Unspecified;
                }
            }

            public override bool Equals(object obj) => obj is InternalFilter other ? Equals(other) : false;

            public bool Equals(InternalFilter other) =>
                Field.Equals(other.Field) && _op == other._op && Equals(_value, other._value);

            public override int GetHashCode() =>
                EqualityHelpers.CombineHashCodes(Field.GetHashCode(), _op, _value?.GetHashCode() ?? -1);
        }
    }
}
