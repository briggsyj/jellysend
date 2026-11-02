"""Tests for pure data classes and logic in davey_compat.py."""
from __future__ import annotations

from unittest.mock import MagicMock, patch

import pytest


# ---------------------------------------------------------------------------
# ProposalsOperationType constants
# ---------------------------------------------------------------------------


def test_proposals_operation_type_values():
    from davey_compat import ProposalsOperationType

    assert ProposalsOperationType.append == 0
    assert ProposalsOperationType.revoke == 1


# ---------------------------------------------------------------------------
# SessionStatus constants
# ---------------------------------------------------------------------------


def test_session_status_values():
    from davey_compat import SessionStatus

    assert SessionStatus.inactive == 0
    assert SessionStatus.pending == 1
    assert SessionStatus.active == 3


# ---------------------------------------------------------------------------
# CommitWelcome
# ---------------------------------------------------------------------------


def test_commit_welcome_stores_commit():
    from davey_compat import CommitWelcome

    data = b"\x01\x02\x03"
    cw = CommitWelcome(data)
    assert cw.commit == data


def test_commit_welcome_welcome_is_none():
    from davey_compat import CommitWelcome

    cw = CommitWelcome(b"anything")
    assert cw.welcome is None


def test_commit_welcome_empty_bytes():
    from davey_compat import CommitWelcome

    cw = CommitWelcome(b"")
    assert cw.commit == b""


# ---------------------------------------------------------------------------
# _EncryptionStats
# ---------------------------------------------------------------------------


def test_encryption_stats_reads_attributes():
    from davey_compat import _EncryptionStats

    raw = MagicMock()
    raw.encrypt_attempts = 10
    raw.encrypt_success_count = 8
    raw.encrypt_failure_count = 2

    stats = _EncryptionStats(raw)
    assert stats.attempts == 10
    assert stats.successes == 8
    assert stats.failures == 2


def test_encryption_stats_missing_attributes_default_zero():
    from davey_compat import _EncryptionStats

    raw = object()  # no encrypt_* attributes
    stats = _EncryptionStats(raw)
    assert stats.attempts == 0
    assert stats.successes == 0
    assert stats.failures == 0


# ---------------------------------------------------------------------------
# process_proposals optype byte prepending logic
# (tests the byte-reassembly without calling into libdave)
# ---------------------------------------------------------------------------


def test_process_proposals_prepends_append_byte():
    """append optype → byte 0x00 prepended before the proposals payload."""
    from davey_compat import DaveSession, ProposalsOperationType

    with patch("davey_compat.dave") as mock_dave:
        mock_session = MagicMock()
        mock_session.process_proposals.return_value = None
        mock_dave.Session.return_value = mock_session
        mock_dave.Encryptor.return_value = MagicMock()
        mock_dave.Codec = MagicMock()

        ds = DaveSession(1, 10, 20)
        proposals_payload = b"\xAB\xCD"
        ds.process_proposals(ProposalsOperationType.append, proposals_payload)

        called_data = mock_session.process_proposals.call_args[0][0]
        assert called_data[0] == 0  # append byte
        assert called_data[1:] == proposals_payload


def test_process_proposals_prepends_revoke_byte():
    """revoke optype → byte 0x01 prepended."""
    from davey_compat import DaveSession, ProposalsOperationType

    with patch("davey_compat.dave") as mock_dave:
        mock_session = MagicMock()
        mock_session.process_proposals.return_value = None
        mock_dave.Session.return_value = mock_session
        mock_dave.Encryptor.return_value = MagicMock()
        mock_dave.Codec = MagicMock()

        ds = DaveSession(1, 10, 20)
        proposals_payload = b"\x01\x02"
        ds.process_proposals(ProposalsOperationType.revoke, proposals_payload)

        called_data = mock_session.process_proposals.call_args[0][0]
        assert called_data[0] == 1  # revoke byte
        assert called_data[1:] == proposals_payload


def test_process_proposals_returns_commit_welcome_when_result():
    from davey_compat import CommitWelcome, DaveSession, ProposalsOperationType

    with patch("davey_compat.dave") as mock_dave:
        mock_session = MagicMock()
        mock_session.process_proposals.return_value = b"\xDE\xAD"
        mock_dave.Session.return_value = mock_session
        mock_dave.Encryptor.return_value = MagicMock()
        mock_dave.Codec = MagicMock()

        ds = DaveSession(1, 10, 20)
        result = ds.process_proposals(ProposalsOperationType.append, b"\x00")
        assert isinstance(result, CommitWelcome)
        assert result.commit == b"\xDE\xAD"


def test_process_proposals_returns_none_when_no_result():
    from davey_compat import DaveSession, ProposalsOperationType

    with patch("davey_compat.dave") as mock_dave:
        mock_session = MagicMock()
        mock_session.process_proposals.return_value = None
        mock_dave.Session.return_value = mock_session
        mock_dave.Encryptor.return_value = MagicMock()
        mock_dave.Codec = MagicMock()

        ds = DaveSession(1, 10, 20)
        result = ds.process_proposals(ProposalsOperationType.append, b"\x00")
        assert result is None


def test_process_proposals_exception_returns_none():
    from davey_compat import DaveSession, ProposalsOperationType

    with patch("davey_compat.dave") as mock_dave:
        mock_session = MagicMock()
        mock_session.process_proposals.side_effect = RuntimeError("boom")
        mock_dave.Session.return_value = mock_session
        mock_dave.Encryptor.return_value = MagicMock()
        mock_dave.Codec = MagicMock()

        ds = DaveSession(1, 10, 20)
        result = ds.process_proposals(ProposalsOperationType.append, b"\x00")
        assert result is None


# ---------------------------------------------------------------------------
# DaveSession.encrypt_opus passthrough when result is None
# ---------------------------------------------------------------------------


def test_encrypt_opus_passthrough_when_no_key():
    from davey_compat import DaveSession

    with patch("davey_compat.dave") as mock_dave:
        mock_encryptor = MagicMock()
        mock_encryptor.encrypt.return_value = None  # no key yet → passthrough
        mock_dave.Session.return_value = MagicMock()
        mock_dave.Encryptor.return_value = mock_encryptor
        mock_dave.Codec = MagicMock()
        mock_dave.MediaType = MagicMock()

        ds = DaveSession(1, 10, 20)
        raw = b"\xAA\xBB"
        assert ds.encrypt_opus(raw) is raw


def test_encrypt_opus_returns_encrypted_when_key_ready():
    from davey_compat import DaveSession

    with patch("davey_compat.dave") as mock_dave:
        encrypted = b"\xFF\xFE"
        mock_encryptor = MagicMock()
        mock_encryptor.encrypt.return_value = encrypted
        mock_dave.Session.return_value = MagicMock()
        mock_dave.Encryptor.return_value = mock_encryptor
        mock_dave.Codec = MagicMock()
        mock_dave.MediaType = MagicMock()

        ds = DaveSession(1, 10, 20)
        assert ds.encrypt_opus(b"\x01") == encrypted


# ---------------------------------------------------------------------------
# DaveSession repr
# ---------------------------------------------------------------------------


def test_dave_session_repr():
    from davey_compat import DaveSession

    with patch("davey_compat.dave") as mock_dave:
        mock_dave.Session.return_value = MagicMock()
        mock_dave.Encryptor.return_value = MagicMock()
        mock_dave.Codec = MagicMock()

        ds = DaveSession(1, 10, 20)
        r = repr(ds)
        assert "DaveSession" in r
        assert "epoch=None" in r
        assert "ready=False" in r


# ---------------------------------------------------------------------------
# DaveSession lifecycle and libdave delegation (dave fully mocked)
# ---------------------------------------------------------------------------

import enum  # noqa: E402
from types import SimpleNamespace  # noqa: E402


class _RejectType(enum.Enum):
    failed = 1
    ignored = 2


@pytest.fixture
def mdave():
    with patch("davey_compat.dave") as mock_dave:
        mock_dave.RejectType = _RejectType
        mock_dave.Session.return_value = MagicMock(name="session")
        mock_dave.Encryptor.return_value = MagicMock(name="encryptor")
        yield mock_dave


@pytest.fixture
def ds(mdave):
    from davey_compat import DaveSession

    return DaveSession(1, 10, 20)


def test_init_wires_session_and_encryptor(mdave, ds):
    from davey_compat import SessionStatus

    sess = mdave.Session.return_value
    enc = mdave.Encryptor.return_value
    mdave.Session.assert_called_once_with(mls_failure_callback=ds._on_mls_failure)
    enc.assign_ssrc_to_codec.assert_called_once_with(0, mdave.Codec.opus)
    enc.set_passthrough_mode.assert_called_once_with(True)
    # group id = channel id, user id passed as a string
    sess.init.assert_called_once_with(1, 20, "10")
    assert ds.status == SessionStatus.pending
    assert ds.ready is False
    assert ds.epoch is None
    assert ds.voice_privacy_code is None


def test_on_mls_failure_prints(ds, capsys):
    ds._on_mls_failure("bad_commit", "detail here")
    assert "MLS failure: bad_commit" in capsys.readouterr().out


def test_recognized_users_self_only_without_voice_state(ds):
    assert ds._get_recognized_users() == {"10"}


def test_recognized_users_includes_channel_members(ds):
    members = [SimpleNamespace(id=100), SimpleNamespace(id=200)]
    ds._voice_state = SimpleNamespace(
        voice_client=SimpleNamespace(channel=SimpleNamespace(members=members))
    )
    assert ds._get_recognized_users() == {"10", "100", "200"}


def test_recognized_users_tolerates_broken_voice_state(ds, capsys):
    ds._voice_state = SimpleNamespace(voice_client=None)  # .channel raises
    assert ds._get_recognized_users() == {"10"}
    assert "Could not read channel members" in capsys.readouterr().out


def test_refresh_key_success_activates(mdave, ds):
    from davey_compat import SessionStatus

    sess = mdave.Session.return_value
    enc = mdave.Encryptor.return_value
    ratchet = object()
    sess.get_key_ratchet.return_value = ratchet
    ds._refresh_key()
    sess.get_key_ratchet.assert_called_once_with("10")
    enc.set_key_ratchet.assert_called_once_with(ratchet)
    enc.set_passthrough_mode.assert_called_with(False)
    assert ds.ready is True
    assert ds.status == SessionStatus.active


def test_refresh_key_none_stays_pending(mdave, ds, capsys):
    from davey_compat import SessionStatus

    mdave.Session.return_value.get_key_ratchet.return_value = None
    ds._refresh_key()
    assert ds.ready is False
    assert ds.status == SessionStatus.pending
    mdave.Encryptor.return_value.set_key_ratchet.assert_not_called()
    assert "get_key_ratchet returned None" in capsys.readouterr().out


def test_reset_clears_state(mdave, ds):
    from davey_compat import SessionStatus

    ds._ready = True
    ds._epoch = 5
    ds.status = SessionStatus.active
    enc = mdave.Encryptor.return_value
    enc.reset_mock()
    ds.reset()
    mdave.Session.return_value.reset.assert_called_once()
    enc.set_key_ratchet.assert_called_once_with(None)
    enc.set_passthrough_mode.assert_called_once_with(True)
    assert ds.ready is False
    assert ds.epoch is None
    assert ds.status == SessionStatus.inactive


def test_reinit_updates_ids_and_reinitialises(mdave, ds):
    from davey_compat import SessionStatus

    sess = mdave.Session.return_value
    ds._ready = True
    ds._epoch = 3
    ds.reinit(2, 11, 21)
    sess.reset.assert_called_once()
    assert sess.init.call_args_list[-1].args == (2, 21, "11")
    mdave.Encryptor.return_value.set_key_ratchet.assert_called_with(None)
    assert ds.ready is False
    assert ds.epoch is None
    assert ds.status == SessionStatus.pending
    assert ds._get_recognized_users() == {"11"}


def test_simple_delegations(mdave, ds):
    sess = mdave.Session.return_value
    enc = mdave.Encryptor.return_value
    sess.get_marshalled_key_package.return_value = b"kp"
    assert ds.get_serialized_key_package() == b"kp"
    ds.set_external_sender(b"ext")
    sess.set_external_sender.assert_called_once_with(b"ext")
    ds.set_passthrough_mode(False, transition_expiry=10)
    enc.set_passthrough_mode.assert_called_with(False)


def test_process_proposals_passes_recognized_users(mdave, ds):
    from davey_compat import ProposalsOperationType

    ds._voice_state = SimpleNamespace(
        voice_client=SimpleNamespace(channel=SimpleNamespace(members=[SimpleNamespace(id=5)]))
    )
    mdave.Session.return_value.process_proposals.return_value = None
    ds.process_proposals(ProposalsOperationType.append, b"")
    assert mdave.Session.return_value.process_proposals.call_args.args[1] == {"10", "5"}


def test_process_commit_rejected_raises(mdave, ds):
    mdave.Session.return_value.process_commit.return_value = _RejectType.failed
    with pytest.raises(RuntimeError, match="rejected: failed"):
        ds.process_commit(b"c")
    mdave.Session.return_value.get_key_ratchet.assert_not_called()


def test_process_commit_dict_sets_epoch_and_refreshes(mdave, ds):
    sess = mdave.Session.return_value
    sess.process_commit.return_value = {1: b"a", 4: b"b", 2: b"c"}
    sess.get_key_ratchet.return_value = object()
    ds.process_commit(b"c")
    assert ds.epoch == 4
    assert ds.ready is True


def test_process_commit_empty_dict_keeps_epoch(mdave, ds):
    sess = mdave.Session.return_value
    sess.process_commit.return_value = {}
    sess.get_key_ratchet.return_value = None
    ds.process_commit(b"c")
    assert ds.epoch is None
    sess.get_key_ratchet.assert_called_once()


def test_process_welcome_exception_reraises(mdave, ds, capsys):
    mdave.Session.return_value.process_welcome.side_effect = ValueError("bad")
    with pytest.raises(ValueError):
        ds.process_welcome(b"w")
    assert "process_welcome FAILED" in capsys.readouterr().out


def test_process_welcome_none_raises(mdave, ds):
    mdave.Session.return_value.process_welcome.return_value = None
    with pytest.raises(RuntimeError, match="welcome rejected"):
        ds.process_welcome(b"w")


def test_process_welcome_success(mdave, ds):
    sess = mdave.Session.return_value
    sess.process_welcome.return_value = {7: b"x"}
    sess.get_key_ratchet.return_value = object()
    ds.process_welcome(b"w")
    assert sess.process_welcome.call_args.args == (b"w", {"10"})
    assert ds.epoch == 7
    assert ds.ready is True


def test_register_video_ssrc(mdave, ds):
    enc = mdave.Encryptor.return_value
    ds.register_video_ssrc(1234)
    enc.assign_ssrc_to_codec.assert_called_with(1234, mdave.Codec.h264)


def test_register_video_ssrc_error_swallowed(mdave, ds, capsys):
    mdave.Encryptor.return_value.assign_ssrc_to_codec.side_effect = RuntimeError("dup")
    ds.register_video_ssrc(1234)  # must not raise
    assert "register_video_ssrc(1234) failed" in capsys.readouterr().out


def test_encrypt_h264(mdave, ds):
    enc = mdave.Encryptor.return_value
    enc.encrypt.return_value = b"enc"
    assert ds.encrypt_h264(55, b"raw") == b"enc"
    enc.encrypt.assert_called_with(mdave.MediaType.video, 55, b"raw")
    enc.encrypt.return_value = None
    raw = b"raw2"
    assert ds.encrypt_h264(55, raw) is raw


def test_get_user_ids_empty(ds):
    assert ds.get_user_ids() == []


def test_get_encryption_stats(mdave, ds):
    mdave.Encryptor.return_value.get_stats.return_value = SimpleNamespace(
        encrypt_attempts=3, encrypt_success_count=2, encrypt_failure_count=1
    )
    s = ds.get_encryption_stats()
    assert (s.attempts, s.successes, s.failures) == (3, 2, 1)
    mdave.Encryptor.return_value.get_stats.assert_called_once_with(mdave.MediaType.audio)


def test_get_encryption_stats_fallback_on_error(mdave, ds):
    mdave.Encryptor.return_value.get_stats.side_effect = RuntimeError
    s = ds.get_encryption_stats()
    assert (s.attempts, s.successes, s.failures) == (0, 0, 0)


def test_decrypt_not_implemented(ds):
    with pytest.raises(RuntimeError, match="not implemented"):
        ds.decrypt(1, None, b"")


# ---------------------------------------------------------------------------
# patch_reinit
# ---------------------------------------------------------------------------


def _fake_voice_state_module(dave_session_factory):
    calls = []

    class VoiceConnectionState:
        dave_session = None

        async def reinit_dave_session(self):
            calls.append(self)
            self.dave_session = dave_session_factory()

    return SimpleNamespace(VoiceConnectionState=VoiceConnectionState), calls


async def test_patch_reinit_injects_back_reference(ds):
    from davey_compat import patch_reinit

    module, calls = _fake_voice_state_module(lambda: ds)
    patch_reinit(module)
    state = module.VoiceConnectionState()
    await state.reinit_dave_session()
    assert calls == [state]  # original still runs
    assert ds._voice_state is state


async def test_patch_reinit_ignores_foreign_session():
    from davey_compat import patch_reinit

    foreign = SimpleNamespace(_voice_state="untouched")
    module, _ = _fake_voice_state_module(lambda: foreign)
    patch_reinit(module)
    state = module.VoiceConnectionState()
    await state.reinit_dave_session()
    assert foreign._voice_state == "untouched"


async def test_patch_reinit_handles_none_session():
    from davey_compat import patch_reinit

    module, calls = _fake_voice_state_module(lambda: None)
    patch_reinit(module)
    state = module.VoiceConnectionState()
    await state.reinit_dave_session()  # must not raise
    assert len(calls) == 1
