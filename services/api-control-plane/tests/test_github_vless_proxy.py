from __future__ import annotations

import github_vless_proxy as proxy


UUID = "11111111-1111-4111-8111-111111111111"


def test_reality_tcp_vision_link_builds_isolated_sing_box_outbound():
    url = (
        f"vless://{UUID}@us.example.com:443"
        "?encryption=none&flow=xtls-rprx-vision&security=reality"
        "&sni=www.microsoft.com&fp=chrome"
        "&pbk=abcdefghijklmnopqrstuvwxyz0123456789ABCDE&sid=0123abcd&type=tcp"
        "#US-Reality"
    )
    outbound, summary = proxy._parse_vless_url(url)
    assert outbound["type"] == "vless"
    assert outbound["server"] == "us.example.com"
    assert outbound["server_port"] == 443
    assert outbound["uuid"] == UUID
    assert outbound["flow"] == "xtls-rprx-vision"
    assert outbound["tls"]["enabled"] is True
    assert outbound["tls"]["server_name"] == "www.microsoft.com"
    assert outbound["tls"]["utls"] == {"enabled": True, "fingerprint": "chrome"}
    assert outbound["tls"]["reality"]["public_key"] == "abcdefghijklmnopqrstuvwxyz0123456789ABCDE"
    assert outbound["tls"]["reality"]["short_id"] == "0123abcd"
    assert "transport" not in outbound
    assert summary == {
        "name": "US-Reality",
        "server": "us.example.com",
        "port": 443,
        "security": "reality",
        "transport": "tcp",
        "flow": "xtls-rprx-vision",
    }


def test_tls_websocket_link_preserves_host_path_and_early_data():
    url = (
        f"vless://{UUID}@cdn.example.com:443"
        "?security=tls&sni=edge.example.com&type=ws&host=origin.example.com"
        "&path=%2Fvless%3Fed%3D2048&ed=2048#WS"
    )
    outbound, summary = proxy._parse_vless_url(url)
    assert outbound["tls"]["server_name"] == "edge.example.com"
    assert outbound["transport"]["type"] == "ws"
    assert outbound["transport"]["path"] == "/vless?ed=2048"
    assert outbound["transport"]["headers"]["Host"] == "origin.example.com"
    assert outbound["transport"]["max_early_data"] == 2048
    assert outbound["transport"]["early_data_header_name"] == "Sec-WebSocket-Protocol"
    assert summary["transport"] == "ws"


def test_grpc_link_and_packet_encoding_are_supported():
    url = (
        f"vless://{UUID}@grpc.example.com:8443"
        "?security=tls&type=grpc&serviceName=my-service&packetEncoding=xudp"
    )
    outbound, summary = proxy._parse_vless_url(url)
    assert outbound["packet_encoding"] == "xudp"
    assert outbound["transport"] == {"type": "grpc", "service_name": "my-service"}
    assert summary["port"] == 8443
    assert summary["transport"] == "grpc"


def test_unsupported_transport_is_rejected_instead_of_silent_direct_fallback():
    url = f"vless://{UUID}@example.com:443?security=tls&type=xhttp"
    try:
        proxy._parse_vless_url(url)
    except ValueError as exc:
        assert "暂不支持此 VLESS 传输类型" in str(exc)
    else:
        raise AssertionError("unsupported VLESS transport must be rejected")


def test_generated_socks_inbound_is_loopback_only_and_has_no_tun():
    url = f"vless://{UUID}@example.com:443?security=tls&type=tcp"
    config, _ = proxy._sing_box_config(url)
    assert config["inbounds"] == [
        {
            "type": "socks",
            "tag": "github-socks",
            "listen": "127.0.0.1",
            "listen_port": proxy.LISTEN_PORT,
        }
    ]
    assert config["route"]["final"] == "vless-out"
    assert all(item.get("type") != "tun" for item in config["inbounds"])
