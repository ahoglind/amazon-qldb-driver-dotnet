﻿/*
 * Copyright 2020 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"). You may not use this file except in compliance with
 * the License. A copy of the License is located at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * or in the "license" file accompanying this file. This file is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
 * CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
 * and limitations under the License.
 */

namespace Amazon.QLDB.Driver.Tests
{
    using System;
    using System.IO;
    using Amazon.QLDBSession.Model;
    using Amazon.Runtime;
    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Moq;

    [TestClass]
    public class SessionPoolTests
    {
        [TestMethod]
        public void Constructor_CreateSessionPool_NewSessionPoolCreated()
        {
            Assert.IsNotNull(new SessionPool(() => { return new Mock<Session>(null, null, null, null, null).Object; }, 
                new Mock<IRetryHandler>().Object, 1, NullLogger.Instance));
        }

        [TestMethod]
        public void GetSession_GetSessionFromPool_NewSessionReturned()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null).Object;
            mockCreator.Setup(x => x()).Returns(mockSession);

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);
            var returnedSession = pool.GetSession();

            Assert.IsNotNull(returnedSession);
            mockCreator.Verify(x => x(), Times.Exactly(1));
        }

        [TestMethod]
        public void GetSession_GetSessionFromPool_ExpectedSessionReturned()
        {
            var mockCreator = new Mock<Func<Session>>();
            var session = new Session(null, null, null, "testSessionId", null);
            mockCreator.Setup(x => x()).Returns(session);

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);
            var returnedSession = pool.GetSession();

            Assert.AreEqual(session.SessionId, returnedSession.GetSessionId());
        }

        [TestMethod]
        public void GetSession_GetTwoSessionsFromPoolOfOne_TimeoutOnSecondGet()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null).Object;
            mockCreator.Setup(x => x()).Returns(mockSession);

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);
            var returnedSession = pool.GetSession();
            Assert.ThrowsException<QldbDriverException>(() => pool.GetSession());

            Assert.IsNotNull(returnedSession);
            mockCreator.Verify(x => x(), Times.Exactly(1));
        }

        [TestMethod]
        public void GetSession_GetTwoSessionsFromPoolOfOneAfterFirstOneDisposed_NoThrowOnSecondGet()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null);
            mockCreator.Setup(x => x()).Returns(mockSession.Object);
            mockSession.Setup(x => x.StartTransaction()).Returns(new StartTransactionResult
            {
                TransactionId = "testTransactionIdddddd"
            });

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);
            var returnedSession = pool.GetSession();
            returnedSession.Release();
            var nextSession = pool.GetSession();
            Assert.IsNotNull(nextSession);

            nextSession.StartTransaction();
            mockCreator.Verify(x => x(), Times.Exactly(1));
        }

        [TestMethod]
        public void GetSession_FailedToCreateSession_ThrowTheOriginalException()
        {
            var mockCreator = new Mock<Func<Session>>();
            var exception = new AmazonServiceException("test");
            mockCreator.Setup(x => x()).Throws(exception);

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);

            Assert.ThrowsException<AmazonServiceException>(() => pool.GetSession());
        }

        [TestMethod]
        public void GetSession_DisposeSession_ShouldNotEndSession()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null);
            mockCreator.Setup(x => x()).Returns(mockSession.Object);

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);

            var returnedSession = pool.GetSession();

            returnedSession.Release();

            mockSession.Verify(s => s.End(), Times.Exactly(0));
        }

        [TestMethod]
        public void Execute_NoException_ReturnFunctionValue()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null);
            mockCreator.Setup(x => x()).Returns(mockSession.Object);
            var retry = new Mock<Action<int>>();

            var sendCommandResponseStart = new StartTransactionResult
            {
                TransactionId = "testTransactionIdddddd"
            };

            var h1 = QldbHash.ToQldbHash("testTransactionIdddddd");

            var sendCommandResponseCommit = new CommitTransactionResult
            {
                CommitDigest = new MemoryStream(h1.Hash),
                TransactionId = "testTransactionIdddddd"
            };

            mockSession.Setup(x => x.StartTransaction())
                .Returns(sendCommandResponseStart);
            mockSession.Setup(x => x.CommitTransaction(It.IsAny<string>(), It.IsAny<MemoryStream>()))
                .Returns(sendCommandResponseCommit);

            var mockFunction = new Mock<Func<TransactionExecutor, int>>();
            mockFunction.Setup(f => f.Invoke(It.IsAny<TransactionExecutor>())).Returns(1);
            var mockRetry = new Mock<Action<int>>();

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);

            pool.Execute(mockFunction.Object, Driver.RetryPolicy.Builder().Build(), retry.Object);

            mockCreator.Verify(x => x(), Times.Once);
            retry.Verify(r => r.Invoke(It.IsAny<int>()), Times.Never);
        }

        [TestMethod]
        public void Execute_HaveOCCExceptionsWithinRetryLimit_Succeeded()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null);
            mockCreator.Setup(x => x()).Returns(mockSession.Object);
            var retry = new Mock<Action<int>>();

            var sendCommandResponseStart = new StartTransactionResult
            {
                TransactionId = "testTransactionIdddddd"
            };

            var h1 = QldbHash.ToQldbHash("testTransactionIdddddd");

            var sendCommandResponseCommit = new CommitTransactionResult
            {
                CommitDigest = new MemoryStream(h1.Hash),
                TransactionId = "testTransactionIdddddd"
            };

            mockSession.Setup(x => x.StartTransaction())
                .Returns(sendCommandResponseStart);
            mockSession.Setup(x => x.CommitTransaction(It.IsAny<string>(), It.IsAny<MemoryStream>()))
                .Returns(sendCommandResponseCommit);

            var mockFunction = new Mock<Func<TransactionExecutor, int>>();
            mockFunction.SetupSequence(f => f.Invoke(It.IsAny<TransactionExecutor>()))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"))
                .Returns(1);
            var mockRetry = new Mock<Action<int>>();

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);

            pool.Execute(mockFunction.Object, Driver.RetryPolicy.Builder().Build(), retry.Object);

            mockCreator.Verify(x => x(), Times.Once);
            retry.Verify(r => r.Invoke(It.IsAny<int>()), Times.Exactly(3));
            Assert.AreEqual(1, pool.AvailablePermit());
        }

        [TestMethod]
        public void Execute_HaveOCCExceptionsAndAbortFailuresWithinRetryLimit_Succeeded()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null);
            mockCreator.Setup(x => x()).Returns(mockSession.Object);
            var retry = new Mock<Action<int>>();

            var sendCommandResponseStart = new StartTransactionResult
            {
                TransactionId = "testTransactionIdddddd"
            };

            var h1 = QldbHash.ToQldbHash("testTransactionIdddddd");

            var sendCommandResponseCommit = new CommitTransactionResult
            {
                CommitDigest = new MemoryStream(h1.Hash),
                TransactionId = "testTransactionIdddddd"
            };

            var abortResponse = new AbortTransactionResult { };
            var serviceException = new AmazonServiceException();

            mockSession.Setup(x => x.StartTransaction())
                .Returns(sendCommandResponseStart);
            mockSession.Setup(x => x.CommitTransaction(It.IsAny<string>(), It.IsAny<MemoryStream>()))
                .Returns(sendCommandResponseCommit);
            mockSession.SetupSequence(x => x.AbortTransaction()).Throws(serviceException).Returns(abortResponse).Throws(serviceException);

            var mockFunction = new Mock<Func<TransactionExecutor, int>>();
            mockFunction.SetupSequence(f => f.Invoke(It.IsAny<TransactionExecutor>()))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"))
                .Returns(1);
            var mockRetry = new Mock<Action<int>>();

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 2, NullLogger.Instance);

            var session1 = pool.GetSession();
            var session2 = pool.GetSession();
            session1.Release();
            session2.Release();

            pool.Execute(mockFunction.Object, Driver.RetryPolicy.Builder().Build(), retry.Object);

            mockCreator.Verify(x => x(), Times.Exactly(2));
            retry.Verify(r => r.Invoke(It.IsAny<int>()), Times.Exactly(3));
            Assert.AreEqual(2, pool.AvailablePermit());
        }

        [TestMethod]
        public void Execute_HaveOCCExceptionsAboveRetryLimit_ThrowOCC()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null);
            mockCreator.Setup(x => x()).Returns(mockSession.Object);
            var retry = new Mock<Action<int>>();

            var sendCommandResponseStart = new StartTransactionResult
            {
                TransactionId = "testTransactionIdddddd"
            };

            var h1 = QldbHash.ToQldbHash("testTransactionIdddddd");

            var sendCommandResponseCommit = new CommitTransactionResult
            {
                CommitDigest = new MemoryStream(h1.Hash),
                TransactionId = "testTransactionIdddddd"
            };

            mockSession.Setup(x => x.StartTransaction())
                .Returns(sendCommandResponseStart);
            mockSession.Setup(x => x.CommitTransaction(It.IsAny<string>(), It.IsAny<MemoryStream>()))
                .Returns(sendCommandResponseCommit);

            var mockFunction = new Mock<Func<TransactionExecutor, int>>();
            mockFunction.SetupSequence(f => f.Invoke(It.IsAny<TransactionExecutor>()))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"))
                .Throws(new OccConflictException("occ"));
            var mockRetry = new Mock<Action<int>>();

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 1, NullLogger.Instance);

            Assert.ThrowsException<OccConflictException>(() => pool.Execute(mockFunction.Object, Driver.RetryPolicy.Builder().Build(), retry.Object));

            mockCreator.Verify(x => x(), Times.Once);
            retry.Verify(r => r.Invoke(It.IsAny<int>()), Times.Exactly(4));
        }

        [TestMethod]
        public void Execute_HaveISE_Succeeded()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession = new Mock<Session>(null, null, null, null, null);
            mockCreator.Setup(x => x()).Returns(mockSession.Object);
            var retry = new Mock<Action<int>>();

            var sendCommandResponseStart = new StartTransactionResult
            {
                TransactionId = "testTransactionIdddddd"
            };

            var h1 = QldbHash.ToQldbHash("testTransactionIdddddd");

            var sendCommandResponseCommit = new CommitTransactionResult
            {
                CommitDigest = new MemoryStream(h1.Hash),
                TransactionId = "testTransactionIdddddd"
            };

            mockSession.Setup(x => x.StartTransaction())
                .Returns(sendCommandResponseStart);
            mockSession.Setup(x => x.CommitTransaction(It.IsAny<string>(), It.IsAny<MemoryStream>()))
                .Returns(sendCommandResponseCommit);

            var mockFunction = new Mock<Func<TransactionExecutor, int>>();
            mockFunction.SetupSequence(f => f.Invoke(It.IsAny<TransactionExecutor>()))
                .Throws(new InvalidSessionException("invalid"))
                .Throws(new InvalidSessionException("invalid"))
                .Throws(new InvalidSessionException("invalid"))
                .Throws(new InvalidSessionException("invalid"))
                .Returns(1);
            var mockRetry = new Mock<Action<int>>();

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 2, NullLogger.Instance);

            var session1 = pool.GetSession();
            var session2 = pool.GetSession();
            session1.Release();
            session2.Release();

            pool.Execute(mockFunction.Object, Driver.RetryPolicy.Builder().Build(), retry.Object);

            mockCreator.Verify(x => x(), Times.Exactly(6));
            retry.Verify(r => r.Invoke(It.IsAny<int>()), Times.Exactly(4));
            Assert.AreEqual(2, pool.AvailablePermit());
        }

        [TestMethod]
        public void Dispose_DisposeSessionPool_DestroyAllSessions()
        {
            var mockCreator = new Mock<Func<Session>>();
            var mockSession1 = new Mock<Session>(null, null, null, null, null);
            var mockSession2 = new Mock<Session>(null, null, null, null, null);
            mockCreator.SetupSequence(x => x()).Returns(mockSession1.Object).Returns(mockSession2.Object);

            var pool = new SessionPool(mockCreator.Object, QldbDriverBuilder.CreateDefaultRetryHandler(NullLogger.Instance), 2, NullLogger.Instance);

            var session1 = pool.GetSession();
            var session2 = pool.GetSession();
            session1.Release();
            session2.Release();

            pool.Dispose();

            mockSession1.Verify(s => s.End(), Times.Exactly(1));
            mockSession2.Verify(s => s.End(), Times.Exactly(1));
        }
    }
}
