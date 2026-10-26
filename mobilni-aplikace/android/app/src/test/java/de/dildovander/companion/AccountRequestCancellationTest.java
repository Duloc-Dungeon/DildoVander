package de.dildovander.companion;

import org.junit.Test;

import java.io.IOException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import static org.junit.Assert.*;

public class AccountRequestCancellationTest {
    @Test public void cancellationAfterAdmissionButBeforeWorkerDispatchPreventsRequest() throws Exception {
        AccountRequestCancellation admitted = new AccountRequestCancellation();
        CountDownLatch dispatchWorker = new CountDownLatch(1);
        AtomicBoolean requestSent = new AtomicBoolean();
        Thread worker = new Thread(() -> {
            try {
                dispatchWorker.await();
                admitted.requireActive();
                requestSent.set(true);
            } catch (IOException expected) { }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
        });
        worker.start();
        admitted.cancel();
        dispatchWorker.countDown();
        worker.join(1000);
        assertFalse(worker.isAlive());
        assertFalse(requestSent.get());
    }

    @Test public void cancellationDuringCsrfPreflightPreventsProtectedRequest() throws Exception {
        AccountRequestCancellation operation = new AccountRequestCancellation();
        CountDownLatch preflightStarted = new CountDownLatch(1);
        CountDownLatch preflightRelease = new CountDownLatch(1);
        AtomicBoolean protectedRequestSent = new AtomicBoolean();
        AtomicBoolean preflightCancelled = new AtomicBoolean();
        Thread worker = new Thread(() -> {
            try {
                AccountRequestCancellation.Cancellable preflight = () -> preflightCancelled.set(true);
                assertTrue(operation.attach(preflight));
                preflightStarted.countDown();
                preflightRelease.await();
                operation.finish(preflight);
                operation.requireActive();
                protectedRequestSent.set(true);
            } catch (IOException expected) { }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
        });
        worker.start();
        assertTrue(preflightStarted.await(1, TimeUnit.SECONDS));
        operation.cancel();
        preflightRelease.countDown();
        worker.join(1000);
        assertFalse(worker.isAlive());
        assertTrue(preflightCancelled.get());
        assertFalse(protectedRequestSent.get());
    }

    @Test public void cancellationBetweenPreflightAndRequestRejectsLateCallPublication() {
        AccountRequestCancellation operation = new AccountRequestCancellation();
        operation.cancel();
        AtomicBoolean requestCancelled = new AtomicBoolean();
        assertFalse(operation.attach(() -> requestCancelled.set(true)));
        assertTrue(requestCancelled.get());
    }

    @Test public void cancellationDuringCreateRequestCancelsCallAndBlocksNextStage() throws Exception {
        AccountRequestCancellation operation = new AccountRequestCancellation();
        CountDownLatch createStarted = new CountDownLatch(1);
        CountDownLatch createRelease = new CountDownLatch(1);
        AtomicBoolean createCancelled = new AtomicBoolean();
        AtomicBoolean liveStreamStarted = new AtomicBoolean();
        Thread worker = new Thread(() -> {
            try {
                AccountRequestCancellation.Cancellable create = () -> createCancelled.set(true);
                assertTrue(operation.attach(create));
                createStarted.countDown();
                createRelease.await();
                operation.finish(create);
                operation.requireActive();
                liveStreamStarted.set(true);
            } catch (IOException expected) { }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
        });
        worker.start();
        assertTrue(createStarted.await(1, TimeUnit.SECONDS));
        operation.cancel();
        createRelease.countDown();
        worker.join(1000);
        assertFalse(worker.isAlive());
        assertTrue(createCancelled.get());
        assertFalse(liveStreamStarted.get());
    }
}
