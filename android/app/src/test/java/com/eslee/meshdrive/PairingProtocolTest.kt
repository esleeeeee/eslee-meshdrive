package com.eslee.meshdrive
import org.junit.Test
import org.junit.Assert.*
class PairingProtocolTest {
    @Test fun pairingSymmetryAndExpiry() {
        val a = PairingProtocol.Side("a", "AA", "nonce1"); val b = PairingProtocol.Side("b", "BB", "nonce2")
        assertEquals(PairingProtocol.sas(a,b), PairingProtocol.sas(b,a)); assertEquals(6, PairingProtocol.sas(a,b).length)
        assertEquals(121000L, PairingProtocol.expires(Long.MAX_VALUE,1000)); assertEquals(2000L, PairingProtocol.expires(2000,1000))
    }
    @Test fun rejectsTraversal() { for (p in listOf("../secret", "C:/secret", "a\\b", "a/..")) { try { PairingProtocol.safeParts(p); fail(p) } catch (_: IllegalArgumentException) {} } }
}
