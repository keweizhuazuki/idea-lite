package dev.idealite.shop;

import java.util.List;
import org.springframework.stereotype.Service;

@Service
public class OrderService {
    private final List<Order> orders = List.of(
        new Order(1001L, "Mechanical keyboard", 399),
        new Order(1002L, "USB-C cable", 29)
    );

    public List<Order> findAll() {
        return orders;
    }

    public Order findById(Long id) {
        return orders.stream()
            .filter(order -> order.id().equals(id))
            .findFirst()
            .orElseThrow();
    }
}
