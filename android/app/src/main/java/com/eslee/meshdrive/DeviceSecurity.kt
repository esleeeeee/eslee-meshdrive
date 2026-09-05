package com.eslee.meshdrive

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.*
import java.security.cert.X509Certificate
import java.util.*
import javax.net.ssl.*

class DeviceSecurity(context: Context) {
    private val preferences = context.getSharedPreferences("identity", 0)
    val id: String = preferences.getString("id", null) ?: UUID.randomUUID().toString().replace("-", "").also { preferences.edit().putString("id", it).commit() }
    private val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    init {
        if (!store.containsAlias("meshdrive-device")) {
            val spec = KeyGenParameterSpec.Builder("meshdrive-device", KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(java.security.spec.ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256)
                .setCertificateSubject(javax.security.auth.x500.X500Principal("CN=eslee MeshDrive Device, SERIALNUMBER=$id"))
                .setCertificateSerialNumber(BigInteger(128, SecureRandom()).max(BigInteger.ONE))
                .setCertificateNotBefore(Date(System.currentTimeMillis() - 300000))
                .setCertificateNotAfter(Date(System.currentTimeMillis() + 10L * 365 * 86400000)).build()
            KeyPairGenerator.getInstance("EC", "AndroidKeyStore").apply { initialize(spec) }.generateKeyPair()
        }
    }
    val certificate = store.getCertificate("meshdrive-device") as X509Certificate
    val fingerprint = fingerprint(certificate)
    fun context(expected: String? = null): SSLContext {
        val keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, null) }
        val trust = object : X509TrustManager {
            override fun getAcceptedIssuers() = emptyArray<X509Certificate>()
            override fun checkClientTrusted(chain: Array<out X509Certificate>, auth: String) = check(chain)
            override fun checkServerTrusted(chain: Array<out X509Certificate>, auth: String) = check(chain)
            private fun check(chain: Array<out X509Certificate>) {
                require(chain.isNotEmpty()); val cert = chain[0]; cert.checkValidity()
                require(cert.publicKey.algorithm == "EC" && cert.subjectX500Principal.name.contains("eslee MeshDrive Device"))
                if (expected != null) require(MessageDigest.isEqual(fingerprint(cert).toByteArray(), expected.toByteArray())) { "인증서가 일치하지 않습니다" }
            }
        }
        return SSLContext.getInstance("TLS").apply { init(keys.keyManagers, arrayOf(trust), SecureRandom()) }
    }
    companion object { fun fingerprint(c: X509Certificate) = MessageDigest.getInstance("SHA-256").digest(c.publicKey.encoded).joinToString("") { "%02X".format(it) } }
}
